using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CDriveMaster.Core.Interfaces;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;
using CDriveMaster.Core.Utilities;

namespace CDriveMaster.Core.Providers;

public class GenericRuleProvider : ICleanupProvider
{
    private const int MaxTraceEntries = 200;
    private const long HeuristicSizeProbeFloorBytes = 128L * 1024L * 1024L;
    private static readonly SemaphoreSlim _probeSemaphore = new(2, 2);
    private static readonly HashSet<string> _unsafeCleanupExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".sys", ".drv", ".ocx", ".cpl", ".scr", ".com", ".msi", ".msp"
    };
    private static readonly EnumerationOptions _fastEnumOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        RecurseSubdirectories = false
    };

    private readonly CleanupRule _rule;
    private readonly IAppDetector _detector;
    private readonly BucketBuilder _bucketBuilder;

    private enum HotspotProbeMode
    {
        Full,
        SeedOnly
    }

    public GenericRuleProvider(CleanupRule rule, IAppDetector detector, BucketBuilder bucketBuilder)
    {
        _rule = rule;
        _detector = detector;
        _bucketBuilder = bucketBuilder;
    }

    public string AppName => _rule.AppName;

    public CleanupRule Rule => _rule;

    public IReadOnlyList<CleanupBucket> GetBuckets()
    {
        var detection = _detector.Detect();
        if (!detection.Found)
        {
            return Array.Empty<CleanupBucket>();
        }

        var buckets = new List<CleanupBucket>();
        string? basePath = detection.BasePath;

        foreach (var target in _rule.Targets)
        {
            string expandedBaseFolder = ExpandBaseFolder(target.BaseFolder);
            string normalizedTarget = NormalizeRelativePath(expandedBaseFolder);

            if (HasWildcardPrefix(normalizedTarget))
            {
                if (string.IsNullOrWhiteSpace(basePath))
                {
                    continue;
                }

                string relativePath = TrimWildcardPrefix(normalizedTarget);

                foreach (var userPath in SafeEnumerateDirectories(basePath))
                {
                    var targetPath = string.IsNullOrWhiteSpace(relativePath)
                        ? userPath
                        : Path.Combine(userPath, relativePath);

                    if (!Directory.Exists(targetPath))
                    {
                        continue;
                    }

                    TryBuildAndAddBucket(buckets, targetPath, target);
                }

                continue;
            }

            string directPath;
            if (Path.IsPathRooted(normalizedTarget))
            {
                directPath = normalizedTarget;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(basePath))
                {
                    continue;
                }

                directPath = Path.Combine(basePath, normalizedTarget);
            }

            TryBuildAndAddBucket(buckets, directPath, target);
        }

        return buckets.AsReadOnly();
    }

    public async Task<FastScanFinding?> ProbeAsync(CleanupRule rule, CancellationToken ct)
    {
        if (rule.FastScan is null)
        {
            return null;
        }

        bool useHeuristicPipeline =
            rule.FastScan.IsExperimental
            || rule.FastScan.HeuristicSearchHints is { Length: > 0 };
        if (useHeuristicPipeline)
        {
            return await ProbeHotspotCoreAsync(rule, ct, HotspotProbeMode.Full);
        }

        await _probeSemaphore.WaitAsync(ct);
        try
        {
            var hint = rule.FastScan;
            var heuristicHints = BuildHeuristicHints(rule, hint);
            bool hasHeuristicHints = heuristicHints.Count > 0;

            if (hint.HotPaths.Count == 0 && !hasHeuristicHints)
            {
                return null;
            }

            long threshold = hint.MinSizeThreshold;
            long totalBytes = 0;
            bool reachedThreshold = false;
            string firstHitPath = string.Empty;
            int maxDepth = Math.Max(0, hint.MaxDepth);
            int scannedFiles = 0;
            var stopwatch = Stopwatch.StartNew();
            var targetDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matchedDirectorySizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var candidateDirectories = new List<string>();
            var verifiedDirectories = new List<string>();
            var rejectedDirectories = new List<string>();
            var rejectReasons = new List<string>();

            foreach (var hotPath in hint.HotPaths.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                ct.ThrowIfCancellationRequested();

                string? expandedPath = ExpandSafePath(hotPath);
                if (string.IsNullOrWhiteSpace(expandedPath) || !DirectoryExistsSafe(expandedPath))
                {
                    continue;
                }

                targetDirectories.Add(expandedPath);
                verifiedDirectories.Add(expandedPath);
                if (string.IsNullOrWhiteSpace(firstHitPath))
                {
                    firstHitPath = expandedPath;
                }
            }

            if (hasHeuristicHints)
            {
                foreach (var heuristicHint in heuristicHints)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(heuristicHint.Parent))
                    {
                        continue;
                    }

                    string? expandedParent = ExpandSafePath(heuristicHint.Parent);
                    if (string.IsNullOrWhiteSpace(expandedParent) || !DirectoryExistsSafe(expandedParent))
                    {
                        continue;
                    }

                    int boundedDepth = Math.Max(0, heuristicHint.MaxDepth);
                    int thresholdScore = Math.Max(1, heuristicHint.ScoreThreshold);
                    var appTokens = heuristicHint.AppTokens
                        .Where(token => !string.IsNullOrWhiteSpace(token))
                        .ToArray();
                    var cacheTokens = heuristicHint.CacheTokens
                        .Where(token => !string.IsNullOrWhiteSpace(token))
                        .ToArray();

                    if (appTokens.Length == 0 && cacheTokens.Length == 0)
                    {
                        continue;
                    }

                    var pending = new Queue<(string Path, int CurrentDepth)>();
                    pending.Enqueue((expandedParent, 0));

                    while (pending.Count > 0)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (stopwatch.ElapsedMilliseconds > 50)
                        {
                            await Task.Delay(1, ct);
                            stopwatch.Restart();
                        }

                        var (currentPath, currentDepth) = pending.Dequeue();

                        if (currentDepth >= boundedDepth)
                        {
                            continue;
                        }

                        IEnumerable<string> subDirs;
                        try
                        {
                            subDirs = Directory.EnumerateDirectories(currentPath, "*", _fastEnumOptions);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            continue;
                        }
                        catch (PathTooLongException)
                        {
                            continue;
                        }
                        catch (IOException)
                        {
                            continue;
                        }

                        foreach (var subDir in subDirs)
                        {
                            ct.ThrowIfCancellationRequested();

                            string dirName = Path.GetFileName(subDir);
                            candidateDirectories.Add(subDir);

                            int score = 0;
                            if (appTokens.Any(token => dirName.Contains(token, StringComparison.OrdinalIgnoreCase)))
                            {
                                score += 2;
                            }

                            bool cacheStructureMatched = false;
                            try
                            {
                                cacheStructureMatched = cacheTokens.Length > 0 &&
                                    Directory.EnumerateDirectories(subDir, "*", _fastEnumOptions)
                                        .Any(childDir =>
                                        {
                                            string childName = Path.GetFileName(childDir);
                                            return cacheTokens.Any(token =>
                                                childName.Contains(token, StringComparison.OrdinalIgnoreCase));
                                        });
                            }
                            catch (UnauthorizedAccessException)
                            {
                            }
                            catch (PathTooLongException)
                            {
                            }
                            catch (IOException)
                            {
                            }

                            if (cacheStructureMatched)
                            {
                                score += 2;
                            }

                            bool hasLargeTokenFile = false;
                            long candidateSizeBytes = 0;
                            try
                            {
                                foreach (var filePath in Directory.EnumerateFiles(subDir, "*", _fastEnumOptions))
                                {
                                    if (stopwatch.ElapsedMilliseconds > 50)
                                    {
                                        await Task.Delay(1, ct);
                                        stopwatch.Restart();
                                    }

                                    string fileName = Path.GetFileName(filePath);
                                    long fileLength;
                                    try
                                    {
                                        fileLength = new FileInfo(filePath).Length;
                                    }
                                    catch (UnauthorizedAccessException)
                                    {
                                        continue;
                                    }
                                    catch (PathTooLongException)
                                    {
                                        continue;
                                    }
                                    catch (IOException)
                                    {
                                        continue;
                                    }

                                    candidateSizeBytes += fileLength;

                                    if (hasLargeTokenFile)
                                    {
                                        continue;
                                    }

                                    bool tokenMatched = appTokens.Any(token => fileName.Contains(token, StringComparison.OrdinalIgnoreCase))
                                        || cacheTokens.Any(token => fileName.Contains(token, StringComparison.OrdinalIgnoreCase));
                                    if (!tokenMatched)
                                    {
                                        continue;
                                    }

                                    if (fileLength > 10L * 1024L * 1024L)
                                    {
                                        hasLargeTokenFile = true;
                                    }
                                }
                            }
                            catch (UnauthorizedAccessException)
                            {
                            }
                            catch (PathTooLongException)
                            {
                            }
                            catch (IOException)
                            {
                            }

                            if (hasLargeTokenFile)
                            {
                                score += 3;
                            }

                            if (candidateSizeBytes > 100L * 1024L * 1024L)
                            {
                                score += 2;
                            }

                            if (candidateSizeBytes > 500L * 1024L * 1024L)
                            {
                                score += 3;
                            }

                            if (score > 0)
                            {
                                if (targetDirectories.Add(subDir))
                                {
                                    verifiedDirectories.Add(subDir);
                                    matchedDirectorySizes[subDir] = candidateSizeBytes;
                                    Debug.WriteLine($"[FuzzyProbe] 启发式命中: {subDir}, Score={score}");
                                }

                                if (string.IsNullOrWhiteSpace(firstHitPath))
                                {
                                    firstHitPath = subDir;
                                }

                                if (score < thresholdScore)
                                {
                                    rejectReasons.Add($"{subDir} -> 评分 {score} 未达阈值 {thresholdScore}，按弱命中保留");
                                }
                            }
                            else
                            {
                                rejectedDirectories.Add(subDir);
                                rejectReasons.Add($"{subDir} -> 评分 {score} 未达阈值 {thresholdScore}");
                            }

                            if (currentDepth + 1 < boundedDepth)
                            {
                                pending.Enqueue((subDir, currentDepth + 1));
                            }
                        }
                    }
                }
            }

            if (targetDirectories.Count == 0)
            {
                rejectReasons.Add("未找到可验证目录");
                var emptyTrace = new ProbeTraceInfo(
                    0,
                    new List<string>(),
                    candidateDirectories,
                    verifiedDirectories,
                    rejectedDirectories,
                    rejectReasons);
                return new FastScanFinding
                {
                    AppId = rule.AppName,
                    SizeBytes = 0,
                    Category = hint.Category,
                    PrimaryPath = null,
                    SourcePath = string.Empty,
                    IsExactSize = true,
                    DisplaySize = SizeFormatter.Format(0),
                    IsExperimental = hint.IsExperimental,
                    Trace = emptyTrace,
                    IsHotspot = false
                };
            }

            var selectedTargetDirectories = PreferLeafDirectories(targetDirectories);

            foreach (var targetDirectory in selectedTargetDirectories)
            {
                ct.ThrowIfCancellationRequested();
                var pending = new Queue<(string path, int depth)>();
                pending.Enqueue((targetDirectory, 0));

                while (pending.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    if (stopwatch.ElapsedMilliseconds > 50)
                    {
                        await Task.Delay(1, ct);
                        stopwatch.Restart();
                    }

                    if ((scannedFiles > 5000 || stopwatch.ElapsedMilliseconds > 800) && totalBytes < threshold)
                    {
                        break;
                    }

                    var (currentPath, depth) = pending.Dequeue();

                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(currentPath, "*", _fastEnumOptions))
                        {
                            ct.ThrowIfCancellationRequested();

                            if (stopwatch.ElapsedMilliseconds > 50)
                            {
                                await Task.Delay(1, ct);
                                stopwatch.Restart();
                            }

                            if (totalBytes >= threshold)
                            {
                                reachedThreshold = true;
                                break;
                            }

                            if ((scannedFiles > 5000 || stopwatch.ElapsedMilliseconds > 800) && totalBytes < threshold)
                            {
                                break;
                            }

                            try
                            {
                                var info = new FileInfo(file);
                                if (info.Exists)
                                {
                                    totalBytes += info.Length;
                                    if (totalBytes >= threshold)
                                    {
                                        reachedThreshold = true;
                                        scannedFiles++;
                                        break;
                                    }
                                }
                            }
                            catch (UnauthorizedAccessException)
                            {
                            }
                            catch (PathTooLongException)
                            {
                            }
                            catch (IOException)
                            {
                            }

                            scannedFiles++;
                        }

                        if (reachedThreshold)
                        {
                            break;
                        }

                        if (depth >= maxDepth)
                        {
                            continue;
                        }

                        foreach (var subDir in Directory.EnumerateDirectories(currentPath, "*", _fastEnumOptions))
                        {
                            pending.Enqueue((subDir, depth + 1));
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                    catch (PathTooLongException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[FuzzyProbe][Warn] 目录遍历异常: {ex.Message}");
                    }
                }

                if (reachedThreshold)
                {
                    break;
                }
            }

            bool isHotspot = totalBytes >= threshold;
            if (!isHotspot)
            {
                foreach (var verifiedDirectory in verifiedDirectories)
                {
                    if (!rejectedDirectories.Contains(verifiedDirectory, StringComparer.OrdinalIgnoreCase))
                    {
                        rejectedDirectories.Add(verifiedDirectory);
                        rejectReasons.Add($"{verifiedDirectory} -> 聚合体积未达热点阈值");
                    }
                }

                rejectReasons.Add($"总大小 {SizeFormatter.Format(totalBytes)} 低于阈值 {SizeFormatter.Format(threshold)}");
                Debug.WriteLine($"[FuzzyProbe] {rule.AppName} 体积过小被淘汰");
            }

            string displaySize = reachedThreshold
                ? $"> {SizeFormatter.Format(threshold)}"
                : SizeFormatter.Format(totalBytes);

            string? primaryPath = ResolveRealPrimaryPath(selectedTargetDirectories, hint.HotPaths);

            var trace = new ProbeTraceInfo(
                0,
                new List<string>(),
                candidateDirectories,
                verifiedDirectories,
                rejectedDirectories,
                rejectReasons);

            CleanupBucket? originalBucket = BuildProbeOriginalBucket(
                rule,
                hint.Category,
                matchedDirectorySizes,
                selectedTargetDirectories,
                totalBytes,
                primaryPath);

            return new FastScanFinding
            {
                AppId = rule.AppName,
                SizeBytes = totalBytes,
                Category = hint.Category,
                PrimaryPath = primaryPath,
                SourcePath = firstHitPath,
                IsExactSize = !reachedThreshold,
                DisplaySize = displaySize,
                IsExperimental = hint.IsExperimental,
                Trace = trace,
                IsHotspot = isHotspot,
                IsHeuristicMatch = hasHeuristicHints,
                OriginalBucket = originalBucket
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            _probeSemaphore.Release();
        }
    }

    private async Task<FastScanFinding?> ProbeHotspotCoreAsync(
        CleanupRule rule,
        CancellationToken ct,
        HotspotProbeMode probeMode)
    {
        if (rule.FastScan is null)
        {
            return null;
        }

        await _probeSemaphore.WaitAsync(ct);
        try
        {
            var hint = rule.FastScan;
            var heuristicHints = BuildHeuristicHints(rule, hint);
            bool hasHeuristicHints = heuristicHints.Count > 0;

            if (hint.HotPaths.Count == 0 && !hasHeuristicHints)
            {
                return null;
            }

            long hotspotThreshold = hint.MinSizeThreshold > 0
                ? hint.MinSizeThreshold
                : 20L * 1024L * 1024L;
            var candidateDirectories = new List<string>();
            var verifiedDirectories = new List<string>();
            var rejectedDirectories = new List<string>();
            var rejectReasons = new List<string>();
            var matchHistory = new List<string>();
            var matchedDirectorySizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var allHeuristicCandidates = new List<HeuristicCandidate>();
            var hotPathDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var hotPath in hint.HotPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                ct.ThrowIfCancellationRequested();

                string? expandedPath = ExpandSafePath(hotPath);
                if (string.IsNullOrWhiteSpace(expandedPath) || !DirectoryExistsSafe(expandedPath))
                {
                    continue;
                }

                hotPathDirectories.Add(expandedPath);
                AddTraceEntry(verifiedDirectories, expandedPath);
                AddTraceEntry(matchHistory, $"直接路径命中 {expandedPath}");
            }

            foreach (var heuristicHint in heuristicHints)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(heuristicHint.Parent))
                {
                    continue;
                }

                string? expandedParent = ExpandSafePath(heuristicHint.Parent);
                if (string.IsNullOrWhiteSpace(expandedParent) || !DirectoryExistsSafe(expandedParent))
                {
                    continue;
                }

                var seedDirectories = BuildPreferredHeuristicSeedDirectories(
                    rule,
                    heuristicHint,
                    expandedParent,
                    allowTopLevelKeywordFallback: probeMode == HotspotProbeMode.Full);
                var discovery = seedDirectories.Count > 0
                    ? await DiscoverHeuristicCandidatesFromSeedsAsync(
                        seedDirectories,
                        heuristicHint,
                        expandedParent,
                        hotspotThreshold,
                        ct)
                    : probeMode == HotspotProbeMode.Full
                        ? await DiscoverHeuristicCandidatesAsync(
                            expandedParent,
                            heuristicHint,
                            hotspotThreshold,
                            ct)
                        : CreateEmptyHeuristicDiscoveryResult();
                AddTraceEntries(candidateDirectories, discovery.CandidateDirectories);
                AddTraceEntries(verifiedDirectories, discovery.VerifiedDirectories);
                AddTraceEntries(rejectedDirectories, discovery.RejectedDirectories);
                AddTraceEntries(rejectReasons, discovery.RejectReasons);
                AddTraceEntries(matchHistory, discovery.MatchHistory);
                allHeuristicCandidates.AddRange(discovery.Candidates);
            }

            HeuristicCandidate? selectedCandidate = SelectPreferredCandidate(allHeuristicCandidates);
            string? primaryPath = null;
            string sourcePath = string.Empty;
            long totalBytes = 0;
            bool isExactSize = true;

            if (selectedCandidate is not null)
            {
                primaryPath = selectedCandidate.Path;
                sourcePath = selectedCandidate.Path;
                totalBytes = selectedCandidate.SizeBytes;
                isExactSize = selectedCandidate.IsExactSize;
                matchedDirectorySizes[selectedCandidate.Path] = selectedCandidate.SizeBytes;

                if (!verifiedDirectories.Contains(selectedCandidate.Path, StringComparer.OrdinalIgnoreCase))
                {
                    AddTraceEntry(verifiedDirectories, selectedCandidate.Path);
                }

                AddTraceEntry(
                    matchHistory,
                    $"叶子优先选择 {selectedCandidate.Path} (得分 {selectedCandidate.Score}，体积 {SizeFormatter.Format(selectedCandidate.SizeBytes)})");

                foreach (var ancestor in allHeuristicCandidates.Where(candidate =>
                             !string.Equals(candidate.Path, selectedCandidate.Path, StringComparison.OrdinalIgnoreCase)
                             && IsAncestorPath(candidate.Path, selectedCandidate.Path)))
                {
                    if (!rejectedDirectories.Contains(ancestor.Path, StringComparer.OrdinalIgnoreCase))
                    {
                        AddTraceEntry(rejectedDirectories, ancestor.Path);
                    }

                    AddTraceEntry(
                        rejectReasons,
                        $"{ancestor.Path} -> 已由更深层叶子目录 {selectedCandidate.Path} 代表，避免父目录重复打包");
                }
            }
            else if (hotPathDirectories.Count > 0)
            {
                var existingHotPaths = hotPathDirectories
                    .Where(DirectoryExistsSafe)
                    .ToList();
                if (existingHotPaths.Count > 0)
                {
                    sourcePath = existingHotPaths[0];
                    primaryPath = ResolveRealPrimaryPath(existingHotPaths, hint.HotPaths);

                    foreach (var hotPath in existingHotPaths)
                    {
                        long sizeBytes = await CalculateDirectorySizeAsync(hotPath, ct);
                        matchedDirectorySizes[hotPath] = sizeBytes;
                        totalBytes += sizeBytes;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(primaryPath) && totalBytes == 0)
            {
                AddTraceEntry(rejectReasons, "未找到可验证目录");
                var emptyTrace = new ProbeTraceInfo(
                    0,
                    new List<string>(),
                    candidateDirectories,
                    verifiedDirectories,
                    rejectedDirectories,
                    rejectReasons,
                    matchHistory,
                    "未发现可用候选路径");

                return new FastScanFinding
                {
                    AppId = rule.AppName,
                    SizeBytes = 0,
                    Category = hint.Category,
                    PrimaryPath = null,
                    SourcePath = string.Empty,
                    IsExactSize = true,
                    DisplaySize = SizeFormatter.Format(0),
                    IsExperimental = hint.IsExperimental,
                    Trace = emptyTrace,
                    IsHotspot = false,
                    IsHeuristicMatch = hasHeuristicHints
                };
            }

            int selectedScore = selectedCandidate?.Score ?? 0;
            int scoreThreshold = selectedCandidate?.ScoreThreshold ?? 1;
            long minCandidateBytes = selectedCandidate?.MinCandidateBytes ?? hotspotThreshold;
            bool meetsScoreThreshold = selectedCandidate is null || selectedScore >= scoreThreshold;
            bool meetsMinCandidateBytes = totalBytes >= minCandidateBytes;
            bool meetsHotspotThreshold = totalBytes >= hotspotThreshold;
            bool isHotspot = meetsHotspotThreshold && meetsMinCandidateBytes && meetsScoreThreshold;

            if (!isHotspot)
            {
                if (selectedCandidate is not null && !meetsScoreThreshold)
                {
                    AddTraceEntry(
                        rejectReasons,
                        $"{selectedCandidate.Path} -> 得分仅 {selectedScore} 分，未达 {scoreThreshold} 分阈值");
                }

                if (selectedCandidate is not null && !meetsMinCandidateBytes)
                {
                    AddTraceEntry(
                        rejectReasons,
                        $"{selectedCandidate.Path} -> 总体积 {SizeFormatter.Format(totalBytes)}，未达 {SizeFormatter.Format(minCandidateBytes)} 候选阈值");
                }

                if (!meetsHotspotThreshold && !string.IsNullOrWhiteSpace(primaryPath))
                {
                    AddTraceEntry(
                        rejectReasons,
                        $"{primaryPath} -> 总体积 {SizeFormatter.Format(totalBytes)}，未达 {SizeFormatter.Format(hotspotThreshold)} 热点阈值");
                }
            }

            string finalDecision = isHotspot
                ? $"已达热点阈值，定位到叶子目录 {primaryPath}"
                : rejectReasons.LastOrDefault(reason => !string.IsNullOrWhiteSpace(reason))
                    ?? "检测到可疑路径，但未达热点阈值";

            string displaySize = isExactSize
                ? SizeFormatter.Format(totalBytes)
                : $"> {SizeFormatter.Format(totalBytes)}";

            var trace = new ProbeTraceInfo(
                0,
                new List<string>(),
                candidateDirectories,
                verifiedDirectories,
                rejectedDirectories,
                rejectReasons,
                matchHistory,
                finalDecision);

            CleanupBucket? originalBucket = isHotspot
                ? BuildProbeOriginalBucket(
                    rule,
                    hint.Category,
                    matchedDirectorySizes,
                    matchedDirectorySizes.Keys,
                    totalBytes,
                    primaryPath)
                : null;

            return new FastScanFinding
            {
                AppId = rule.AppName,
                SizeBytes = totalBytes,
                Category = hint.Category,
                PrimaryPath = primaryPath,
                SourcePath = sourcePath,
                IsExactSize = isExactSize,
                DisplaySize = displaySize,
                IsExperimental = hint.IsExperimental,
                Trace = trace,
                IsHotspot = isHotspot,
                IsHeuristicMatch = hasHeuristicHints,
                OriginalBucket = originalBucket
            };
        }
        finally
        {
            _probeSemaphore.Release();
        }
    }

    public async Task<FastScanFinding?> ProbeSeedOnlyAsync(CleanupRule rule, CancellationToken ct)
    {
        if (rule.FastScan is null)
        {
            return null;
        }

        bool useHeuristicPipeline =
            rule.FastScan.IsExperimental
            || rule.FastScan.HeuristicSearchHints is { Length: > 0 };
        if (!useHeuristicPipeline)
        {
            return await ProbeAsync(rule, ct);
        }

        return await ProbeHotspotCoreAsync(rule, ct, HotspotProbeMode.SeedOnly);
    }

    private sealed record HeuristicCandidate(
        string Path,
        int Depth,
        int Score,
        int ScoreThreshold,
        long SizeBytes,
        long MinCandidateBytes,
        bool IsExactSize,
        List<string> MatchHistory);

    private sealed record HeuristicDiscoveryResult(
        List<HeuristicCandidate> Candidates,
        List<string> CandidateDirectories,
        List<string> VerifiedDirectories,
        List<string> RejectedDirectories,
        List<string> RejectReasons,
        List<string> MatchHistory);

    private sealed record HeuristicSeedDirectory(
        string Path,
        int Depth);

    private async Task<HeuristicDiscoveryResult> DiscoverHeuristicCandidatesFromSeedsAsync(
        IReadOnlyList<HeuristicSeedDirectory> seedDirectories,
        HeuristicSearchHint heuristicHint,
        string expandedParent,
        long hotspotThreshold,
        CancellationToken ct)
    {
        var aggregate = CreateEmptyHeuristicDiscoveryResult();

        foreach (var seedDirectory in seedDirectories)
        {
            ct.ThrowIfCancellationRequested();

            var discovery = await DiscoverHeuristicCandidatesAsync(
                seedDirectory.Path,
                heuristicHint,
                hotspotThreshold,
                ct,
                seedDirectory.Depth,
                evaluateCurrentPath: true);

            AddHeuristicDiscoveryResult(aggregate, discovery);
        }

        return aggregate;
    }

    private async Task<HeuristicDiscoveryResult> DiscoverHeuristicCandidatesAsync(
        string expandedParent,
        HeuristicSearchHint heuristicHint,
        long hotspotThreshold,
        CancellationToken ct,
        int startDepth = 0,
        bool evaluateCurrentPath = false)
    {
        var result = CreateEmptyHeuristicDiscoveryResult();
        int boundedDepth = Math.Max(1, heuristicHint.MaxDepth);
        var pending = new Queue<(string Path, int CurrentDepth, bool EvaluateCurrentPath)>();
        pending.Enqueue((expandedParent, startDepth, evaluateCurrentPath));
        var stopwatch = Stopwatch.StartNew();

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            if (stopwatch.ElapsedMilliseconds > 50)
            {
                await Task.Yield();
                stopwatch.Restart();
            }

            var (currentPath, currentDepth, shouldEvaluateCurrentPath) = pending.Dequeue();
            if (shouldEvaluateCurrentPath)
            {
                await EvaluateAndCollectHeuristicCandidateAsync(
                    currentPath,
                    currentDepth,
                    heuristicHint,
                    hotspotThreshold,
                    result,
                    ct);
            }

            if (currentDepth >= boundedDepth)
            {
                continue;
            }

            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(currentPath, "*", _fastEnumOptions);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (PathTooLongException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var subDir in subDirs)
            {
                ct.ThrowIfCancellationRequested();
                AddTraceEntry(result.CandidateDirectories, subDir);

                await EvaluateAndCollectHeuristicCandidateAsync(
                    subDir,
                    currentDepth + 1,
                    heuristicHint,
                    hotspotThreshold,
                    result,
                    ct);

                // Keep descending even through neutral intermediate folders so we can still
                // reach deeper cache leaves such as "...\\Default\\Cache" or "...\\pdf-cache".
                if (currentDepth + 1 < boundedDepth)
                {
                    pending.Enqueue((subDir, currentDepth + 1, false));
                }
            }
        }

        return result;
    }

    private async Task<HeuristicCandidate> EvaluateHeuristicCandidateAsync(
        string candidatePath,
        int depth,
        HeuristicSearchHint hint,
        long hotspotThreshold,
        CancellationToken ct)
    {
        int score = 0;
        var history = new List<string>();
        string directoryName = Path.GetFileName(candidatePath);
        var pathSegments = candidatePath
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

        string[] appTokens = hint.AppTokens
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] cacheTokens = hint.CacheTokens
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] fileMarkers = hint.FileMarkersAny
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string? matchedPathSegment = pathSegments.FirstOrDefault(segment =>
            appTokens.Any(token => segment.Contains(token, StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrWhiteSpace(matchedPathSegment))
        {
            score += 2;
            history.Add($"路径分段命中 {matchedPathSegment} (+2) @ {candidatePath}");
        }

        string? matchedDirectoryToken = appTokens.FirstOrDefault(token =>
            directoryName.Contains(token, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(matchedDirectoryToken))
        {
            score += 2;
            history.Add($"目录名命中 {matchedDirectoryToken} (+2) @ {candidatePath}");
        }

        try
        {
            foreach (var childDir in Directory.EnumerateDirectories(candidatePath, "*", _fastEnumOptions))
            {
                ct.ThrowIfCancellationRequested();
                string childName = Path.GetFileName(childDir);
                string? matchedStructureToken = cacheTokens.FirstOrDefault(token =>
                    childName.Contains(token, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(matchedStructureToken))
                {
                    continue;
                }

                score += 2;
                history.Add($"结构特征命中 {matchedStructureToken} (+2) @ {candidatePath}");
                break;
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (PathTooLongException)
        {
        }
        catch (IOException)
        {
        }

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(candidatePath, "*", _fastEnumOptions))
            {
                ct.ThrowIfCancellationRequested();
                string fileName = Path.GetFileName(filePath);
                string? matchedFileToken = appTokens.FirstOrDefault(token =>
                        fileName.Contains(token, StringComparison.OrdinalIgnoreCase))
                    ?? fileMarkers.FirstOrDefault(marker =>
                        fileName.Contains(marker, StringComparison.OrdinalIgnoreCase));

                if (string.IsNullOrWhiteSpace(matchedFileToken))
                {
                    continue;
                }

                score += 3;
                history.Add($"文件名命中 {matchedFileToken} (+3) @ {candidatePath}");
                break;
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (PathTooLongException)
        {
        }
        catch (IOException)
        {
        }

        if (score <= 0)
        {
            if (history.Count == 0)
            {
                history.Add($"未命中任何启发式信号 @ {candidatePath}");
            }

            return new HeuristicCandidate(
                candidatePath,
                depth,
                score,
                Math.Max(1, hint.ScoreThreshold),
                0,
                hint.MinCandidateBytes > 0 ? hint.MinCandidateBytes : 20L * 1024L * 1024L,
                true,
                history);
        }

        long sizeProbeLimit = Math.Max(
            Math.Max(hint.MinCandidateBytes, hotspotThreshold),
            HeuristicSizeProbeFloorBytes);
        var sizeProbe = await CalculateDirectorySizeProbeAsync(candidatePath, sizeProbeLimit, ct);
        long sizeBytes = sizeProbe.SizeBytes;
        if (sizeBytes > 100L * 1024L * 1024L)
        {
            score += 3;
            history.Add($"体积超过 100MB (+3) @ {candidatePath}");
        }

        if (sizeProbe.IsExactSize && sizeBytes > 500L * 1024L * 1024L)
        {
            score += 2;
            history.Add($"体积超过 500MB 额外加权 (+2) @ {candidatePath}");
        }
        else if (!sizeProbe.IsExactSize)
        {
            history.Add($"快速估算已达到 {SizeFormatter.Format(sizeBytes)}，跳过剩余体积递归 @ {candidatePath}");
        }

        return new HeuristicCandidate(
            candidatePath,
            depth,
            score,
            Math.Max(1, hint.ScoreThreshold),
            sizeBytes,
            hint.MinCandidateBytes > 0 ? hint.MinCandidateBytes : 20L * 1024L * 1024L,
            sizeProbe.IsExactSize,
            history);
    }

    private static HeuristicCandidate? SelectPreferredCandidate(IReadOnlyList<HeuristicCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var selected = candidates
            .OrderByDescending(candidate => candidate.Score >= candidate.ScoreThreshold)
            .ThenByDescending(candidate => candidate.SizeBytes >= candidate.MinCandidateBytes)
            .ThenByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.SizeBytes)
            .ThenByDescending(candidate => candidate.Depth)
            .First();

        while (true)
        {
            var descendant = candidates
                .Where(candidate =>
                    !string.Equals(candidate.Path, selected.Path, StringComparison.OrdinalIgnoreCase)
                    && IsAncestorPath(selected.Path, candidate.Path)
                    && candidate.Score > 0
                    && (selected.SizeBytes == 0 || candidate.SizeBytes >= (long)(selected.SizeBytes * 0.6d)))
                .OrderByDescending(candidate => candidate.Depth)
                .ThenByDescending(candidate => candidate.Score >= candidate.ScoreThreshold)
                .ThenByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate => candidate.SizeBytes)
                .FirstOrDefault();

            if (descendant is null)
            {
                break;
            }

            selected = descendant;
        }

        return selected;
    }

    private static IReadOnlyList<string> PreferLeafDirectories(IEnumerable<string> directories)
    {
        var distinctDirectories = directories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return distinctDirectories
            .Where(path => !distinctDirectories.Any(otherPath =>
                !string.Equals(path, otherPath, StringComparison.OrdinalIgnoreCase)
                && IsAncestorPath(path, otherPath)))
            .ToList();
    }

    private static HeuristicDiscoveryResult CreateEmptyHeuristicDiscoveryResult()
    {
        return new HeuristicDiscoveryResult(
            new List<HeuristicCandidate>(),
            new List<string>(),
            new List<string>(),
            new List<string>(),
            new List<string>(),
            new List<string>());
    }

    private static void AddHeuristicDiscoveryResult(
        HeuristicDiscoveryResult target,
        HeuristicDiscoveryResult source)
    {
        target.Candidates.AddRange(source.Candidates);
        AddTraceEntries(target.CandidateDirectories, source.CandidateDirectories);
        AddTraceEntries(target.VerifiedDirectories, source.VerifiedDirectories);
        AddTraceEntries(target.RejectedDirectories, source.RejectedDirectories);
        AddTraceEntries(target.RejectReasons, source.RejectReasons);
        AddTraceEntries(target.MatchHistory, source.MatchHistory);
    }

    private async Task EvaluateAndCollectHeuristicCandidateAsync(
        string candidatePath,
        int depth,
        HeuristicSearchHint heuristicHint,
        long hotspotThreshold,
        HeuristicDiscoveryResult result,
        CancellationToken ct)
    {
        var candidate = await EvaluateHeuristicCandidateAsync(
            candidatePath,
            depth,
            heuristicHint,
            hotspotThreshold,
            ct);

        AddTraceEntries(result.MatchHistory, candidate.MatchHistory);
        if (candidate.Score > 0)
        {
            result.Candidates.Add(candidate);
            AddTraceEntry(result.VerifiedDirectories, candidatePath);
        }
        else
        {
            AddTraceEntry(result.RejectedDirectories, candidatePath);
            AddTraceEntry(result.RejectReasons, $"{candidatePath} -> 未命中核心 Token、文件标记或结构特征");
        }
    }

    private IReadOnlyList<HeuristicSeedDirectory> BuildPreferredHeuristicSeedDirectories(
        CleanupRule rule,
        HeuristicSearchHint heuristicHint,
        string expandedParent,
        bool allowTopLevelKeywordFallback)
    {
        var seedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRuleBasedSeedPaths(seedPaths, rule, expandedParent);

        if (allowTopLevelKeywordFallback && seedPaths.Count == 0)
        {
            AddTopLevelKeywordSeedPaths(seedPaths, rule, heuristicHint, expandedParent);
        }

        return seedPaths
            .Select(path => new HeuristicSeedDirectory(path, GetRelativeDepth(expandedParent, path)))
            .OrderBy(seed => seed.Depth)
            .ThenBy(seed => seed.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void AddRuleBasedSeedPaths(
        ISet<string> seedPaths,
        CleanupRule rule,
        string expandedParent)
    {
        foreach (var target in rule.Targets)
        {
            AddSeedPathIfRelevant(seedPaths, ExpandBaseFolder(target.BaseFolder), expandedParent);
        }

        if (rule.FastScan?.HotPaths is null)
        {
            return;
        }

        foreach (var hotPath in rule.FastScan.HotPaths)
        {
            string? expandedHotPath = ExpandSafePath(hotPath);
            if (!string.IsNullOrWhiteSpace(expandedHotPath))
            {
                AddSeedPathIfRelevant(seedPaths, expandedHotPath, expandedParent);
            }
        }
    }

    private void AddTopLevelKeywordSeedPaths(
        ISet<string> seedPaths,
        CleanupRule rule,
        HeuristicSearchHint heuristicHint,
        string expandedParent)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in heuristicHint.AppTokens)
        {
            AddKeyword(keywords, token);
        }

        foreach (var keyword in rule.AppMatchKeywords ?? Array.Empty<string>())
        {
            AddKeyword(keywords, keyword);
        }

        if (keywords.Count == 0)
        {
            return;
        }

        foreach (var subDir in SafeEnumerateDirectories(expandedParent))
        {
            string leafName = Path.GetFileName(subDir);
            if (keywords.Any(keyword => leafName.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                seedPaths.Add(subDir);
            }
        }
    }

    private void AddSeedPathIfRelevant(
        ISet<string> seedPaths,
        string candidatePath,
        string expandedParent)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return;
        }

        string? existingPath = FindExistingSeedPathUnderParent(expandedParent, candidatePath);
        if (!string.IsNullOrWhiteSpace(existingPath))
        {
            seedPaths.Add(existingPath);
        }
    }

    private static string? FindExistingSeedPathUnderParent(string expandedParent, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(expandedParent) || string.IsNullOrWhiteSpace(candidatePath))
        {
            return null;
        }

        string normalizedParent = expandedParent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? current = candidatePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        while (!string.IsNullOrWhiteSpace(current)
            && current.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase))
        {
            if (DirectoryExistsSafe(current))
            {
                return current;
            }

            if (string.Equals(current, normalizedParent, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    private static int GetRelativeDepth(string rootPath, string candidatePath)
    {
        string normalizedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedCandidate = candidatePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        string relativePath = normalizedCandidate[normalizedRoot.Length..]
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return 0;
        }

        return relativePath
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .Length;
    }

    private static void AddKeyword(ISet<string> keywords, string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return;
        }

        string value = rawValue.Trim();
        if (value.Length < 3)
        {
            return;
        }

        keywords.Add(value);
    }

    private static bool IsAncestorPath(string ancestorPath, string descendantPath)
    {
        if (string.IsNullOrWhiteSpace(ancestorPath) || string.IsNullOrWhiteSpace(descendantPath))
        {
            return false;
        }

        string normalizedAncestor = ancestorPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedDescendant = descendantPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedDescendant.StartsWith(
            normalizedAncestor + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void AddTraceEntries(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            AddTraceEntry(target, value);
        }
    }

    private static void AddTraceEntry(List<string> target, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (target.Count >= MaxTraceEntries)
        {
            if (target.Count == MaxTraceEntries)
            {
                target.Add("……更多记录已省略");
            }

            return;
        }

        target.Add(value);
    }

    private static List<HeuristicSearchHint> BuildHeuristicHints(CleanupRule rule, FastScanHint hint)
    {
        if (hint.HeuristicSearchHints is { Length: > 0 })
        {
            return hint.HeuristicSearchHints.ToList();
        }

        var appTokens = (rule.AppMatchKeywords ?? Array.Empty<string>())
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToArray();

        var fallbackHints = new List<HeuristicSearchHint>();
        if (hint.SearchHints is null)
        {
            return fallbackHints;
        }

        foreach (var searchHint in hint.SearchHints)
        {
            var cacheTokens = searchHint.ChildMarkersAny
                .Concat(searchHint.FileMarkersAny)
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            fallbackHints.Add(new HeuristicSearchHint
            {
                Parent = searchHint.Parent,
                AppTokens = searchHint.DirectoryKeywords
                    .Concat(appTokens)
                    .Where(token => !string.IsNullOrWhiteSpace(token))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                CacheTokens = cacheTokens,
                FileMarkersAny = searchHint.FileMarkersAny
                    .Where(token => !string.IsNullOrWhiteSpace(token))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                MaxDepth = searchHint.MaxDepth,
                ScoreThreshold = 5,
                MinCandidateBytes = searchHint.MinCandidateBytes
            });
        }

        return fallbackHints;
    }

    private static CleanupBucket? BuildProbeOriginalBucket(
        CleanupRule rule,
        string category,
        Dictionary<string, long> matchedDirectorySizes,
        IEnumerable<string> targetDirectories,
        long totalBytes,
        string? primaryPath)
    {
        var normalizedCategory = string.IsNullOrWhiteSpace(category) ? "AppCache" : category;

        if (matchedDirectorySizes.Count > 0)
        {
            var entries = matchedDirectorySizes
                .Select(kv => new CleanupEntry(
                    Path: kv.Key,
                    IsDirectory: true,
                    SizeBytes: kv.Value,
                    LastWriteTimeUtc: DateTime.UtcNow,
                    Category: normalizedCategory))
                .ToList();

            long matchedTotalBytes = entries.Sum(entry => entry.SizeBytes);
            string rootPath = entries[0].Path;

            return new CleanupBucket(
                BucketId: $"heuristic:{rule.AppName}:{Guid.NewGuid():N}",
                Category: normalizedCategory,
                RootPath: rootPath,
                AppName: rule.AppName,
                RiskLevel: RiskLevel.SafeWithPreview,
                SuggestedAction: rule.DefaultAction,
                Description: "Heuristic hotspot candidate",
                EstimatedSizeBytes: matchedTotalBytes,
                Entries: entries.AsReadOnly(),
                AllowedRoots: entries
                    .Select(entry => entry.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray());
        }

        string? fallbackPath = primaryPath;
        if (string.IsNullOrWhiteSpace(fallbackPath))
        {
            fallbackPath = targetDirectories.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        }

        if (string.IsNullOrWhiteSpace(fallbackPath))
        {
            return null;
        }

        var fallbackEntry = new CleanupEntry(
            Path: fallbackPath,
            IsDirectory: true,
            SizeBytes: Math.Max(0, totalBytes),
            LastWriteTimeUtc: DateTime.UtcNow,
            Category: normalizedCategory);

        return new CleanupBucket(
            BucketId: $"probe:{rule.AppName}:{Guid.NewGuid():N}",
            Category: normalizedCategory,
            RootPath: fallbackPath,
            AppName: rule.AppName,
            RiskLevel: RiskLevel.SafeWithPreview,
            SuggestedAction: rule.DefaultAction,
            Description: "Probe hotspot candidate",
            EstimatedSizeBytes: Math.Max(0, totalBytes),
            Entries: new List<CleanupEntry> { fallbackEntry }.AsReadOnly(),
            AllowedRoots: new[] { fallbackPath });
    }

    public async Task<List<FastScanFinding>> ScanResiduesAsync(CleanupRule rule, CancellationToken ct)
    {
        var findings = new List<FastScanFinding>();
        if (rule.ResidualFingerprints is null || rule.ResidualFingerprints.Count == 0)
        {
            return findings;
        }

        foreach (var fingerprint in rule.ResidualFingerprints)
        {
            ct.ThrowIfCancellationRequested();

            string? parentPath = ExpandSafePath(fingerprint.Parent);
            if (string.IsNullOrWhiteSpace(parentPath) || !DirectoryExistsSafe(parentPath))
            {
                continue;
            }

            int maxDepth = Math.Max(0, fingerprint.MaxDepth);
            long minSizeBytes = fingerprint.MinSizeBytes > 0 ? fingerprint.MinSizeBytes : 20971520;
            var keywordSet = new HashSet<string>(
                (fingerprint.PathKeywords ?? new List<string>())
                    .Where(keyword => !string.IsNullOrWhiteSpace(keyword)),
                StringComparer.OrdinalIgnoreCase);

            if (keywordSet.Count == 0)
            {
                continue;
            }

            var pending = new Queue<(string Path, int Depth)>();
            pending.Enqueue((parentPath, 0));
            var stopwatch = Stopwatch.StartNew();

            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                if (stopwatch.ElapsedMilliseconds > 50)
                {
                    await Task.Yield();
                    stopwatch.Restart();
                }

                var (currentPath, currentDepth) = pending.Dequeue();
                if (currentDepth >= maxDepth)
                {
                    continue;
                }

                IEnumerable<string> subDirs;
                try
                {
                    subDirs = Directory.EnumerateDirectories(currentPath, "*", _fastEnumOptions);
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (PathTooLongException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }

                foreach (var subDir in subDirs)
                {
                    ct.ThrowIfCancellationRequested();

                    string leafName = new DirectoryInfo(subDir).Name;
                    if (keywordSet.Contains(leafName))
                    {
                        var cleanupEntries = CollectSafeCleanupEntries(subDir, "ResidualCache");
                        long safeSizeBytes = cleanupEntries.Sum(entry => entry.SizeBytes);
                        if (safeSizeBytes >= minSizeBytes)
                        {
                            findings.Add(new FastScanFinding
                            {
                                AppId = rule.AppName,
                                SizeBytes = safeSizeBytes,
                                Category = "ResidualCache",
                                PrimaryPath = subDir,
                                SourcePath = subDir,
                                IsExactSize = true,
                                DisplaySize = SizeFormatter.Format(safeSizeBytes),
                                IsExperimental = rule.FastScan?.IsExperimental ?? false,
                                Trace = new ProbeTraceInfo(
                                    0,
                                    new List<string>(),
                                    new List<string> { subDir },
                                    new List<string> { subDir },
                                    new List<string>(),
                                    new List<string>()),
                                IsHotspot = true,
                                IsResidual = true,
                                OriginalBucket = cleanupEntries.Count == 0
                                    ? null
                                    : new CleanupBucket(
                                        BucketId: $"residual:{rule.AppName}:{Guid.NewGuid():N}",
                                        Category: "ResidualCache",
                                        RootPath: subDir,
                                        AppName: rule.AppName,
                                        RiskLevel: RiskLevel.SafeWithPreview,
                                        SuggestedAction: rule.DefaultAction,
                                        Description: $"Residual hotspot - {new DirectoryInfo(subDir).Name}",
                                        EstimatedSizeBytes: safeSizeBytes,
                                        Entries: cleanupEntries,
                                        AllowedRoots: cleanupEntries
                                            .Select(entry => entry.Path)
                                            .ToArray())
                            });
                        }

                        // Stop descending this matched branch to avoid nested duplicate findings.
                        continue;
                    }

                    pending.Enqueue((subDir, currentDepth + 1));
                }
            }
        }

        return findings;
    }

    private static IReadOnlyList<CleanupEntry> CollectSafeCleanupEntries(string rootPath, string category)
    {
        var entries = new List<CleanupEntry>();
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            string currentPath = pending.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(currentPath, "*", _fastEnumOptions);
            }
            catch (UnauthorizedAccessException)
            {
                files = Array.Empty<string>();
            }
            catch (PathTooLongException)
            {
                files = Array.Empty<string>();
            }
            catch (IOException)
            {
                files = Array.Empty<string>();
            }

            foreach (var filePath in files)
            {
                if (!IsSafeCleanupFile(filePath))
                {
                    continue;
                }

                try
                {
                    var fileInfo = new FileInfo(filePath);
                    if (!fileInfo.Exists)
                    {
                        continue;
                    }

                    entries.Add(new CleanupEntry(
                        Path: fileInfo.FullName,
                        IsDirectory: false,
                        SizeBytes: fileInfo.Length,
                        LastWriteTimeUtc: fileInfo.LastWriteTimeUtc,
                        Category: category));
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (PathTooLongException)
                {
                }
                catch (IOException)
                {
                }
            }

            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(currentPath, "*", _fastEnumOptions);
            }
            catch (UnauthorizedAccessException)
            {
                subDirs = Array.Empty<string>();
            }
            catch (PathTooLongException)
            {
                subDirs = Array.Empty<string>();
            }
            catch (IOException)
            {
                subDirs = Array.Empty<string>();
            }

            foreach (var subDir in subDirs)
            {
                pending.Push(subDir);
            }
        }

        return entries
            .DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    private static bool IsSafeCleanupFile(string filePath)
    {
        string extension = Path.GetExtension(filePath);
        return string.IsNullOrWhiteSpace(extension) || !_unsafeCleanupExtensions.Contains(extension);
    }

    private static string? ExpandSafePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        try
        {
            string normalized = rawPath.Replace('/', '\\');
            string expanded = ExpandKnownFolderTokens(normalized);
            expanded = Environment.ExpandEnvironmentVariables(expanded);
            return string.IsNullOrWhiteSpace(expanded) ? null : expanded;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool DirectoryExistsSafe(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool CandidateMatchesMarkers(
        string candidateDirectory,
        string[] childMarkers,
        string[] fileMarkers,
        CancellationToken ct)
    {
        if (childMarkers.Length == 0 && fileMarkers.Length == 0)
        {
            return true;
        }

        try
        {
            bool directoryMatched = childMarkers.Length > 0 &&
                Directory.EnumerateDirectories(candidateDirectory, "*", _fastEnumOptions)
                    .Any(subDir =>
                    {
                        ct.ThrowIfCancellationRequested();
                        string name = Path.GetFileName(subDir);
                        return childMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));
                    });

            if (directoryMatched)
            {
                return true;
            }

            bool fileMatched = fileMarkers.Length > 0 &&
                Directory.EnumerateFiles(candidateDirectory, "*", _fastEnumOptions)
                    .Any(file =>
                    {
                        ct.ThrowIfCancellationRequested();
                        string name = Path.GetFileName(file);
                        return fileMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));
                    });

            if (fileMatched)
            {
                return true;
            }
        }
        catch (UnauthorizedAccessException)
        {
            Debug.WriteLine($"[FuzzyProbe][Warn] 无权限访问候选目录: {candidateDirectory}");
        }
        catch (PathTooLongException)
        {
            Debug.WriteLine($"[FuzzyProbe][Warn] 候选目录路径过长: {candidateDirectory}");
        }
        catch (IOException)
        {
            Debug.WriteLine($"[FuzzyProbe][Warn] 候选目录 IO 异常: {candidateDirectory}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FuzzyProbe][Warn] 候选目录异常: {ex.Message}");
        }

        return false;
    }

    private static async Task<long> CalculateDirectorySizeAsync(string rootPath, CancellationToken ct)
    {
        var result = await CalculateDirectorySizeProbeAsync(rootPath, long.MaxValue, ct);
        return result.SizeBytes;
    }

    private static async Task<(long SizeBytes, bool IsExactSize)> CalculateDirectorySizeProbeAsync(
        string rootPath,
        long stopAfterBytes,
        CancellationToken ct)
    {
        long totalBytes = 0;
        var pending = new Queue<string>();
        pending.Enqueue(rootPath);
        var stopwatch = Stopwatch.StartNew();
        bool isExactSize = true;

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            if (stopwatch.ElapsedMilliseconds > 50)
            {
                await Task.Yield();
                stopwatch.Restart();
            }

            string currentPath = pending.Dequeue();
            try
            {
                foreach (var file in Directory.EnumerateFiles(currentPath, "*", _fastEnumOptions))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        totalBytes += new FileInfo(file).Length;
                        if (totalBytes >= stopAfterBytes)
                        {
                            isExactSize = false;
                            return (totalBytes, isExactSize);
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                    catch (PathTooLongException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                }

                foreach (var subDir in Directory.EnumerateDirectories(currentPath, "*", _fastEnumOptions))
                {
                    pending.Enqueue(subDir);
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (PathTooLongException)
            {
            }
            catch (IOException)
            {
            }
        }

        return (totalBytes, isExactSize);
    }

    private static string ExpandBaseFolder(string baseFolder)
    {
        if (string.IsNullOrWhiteSpace(baseFolder))
        {
            return string.Empty;
        }

        string normalized = baseFolder.Replace('/', '\\');
        return Environment.ExpandEnvironmentVariables(ExpandKnownFolderTokens(normalized));
    }

    private static string ExpandKnownFolderTokens(string path)
    {
        return path
            .Replace(
                "%LOCALAPPDATA%",
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                StringComparison.OrdinalIgnoreCase)
            .Replace(
                "%APPDATA%",
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                StringComparison.OrdinalIgnoreCase)
            .Replace(
                "%PROGRAMDATA%",
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                StringComparison.OrdinalIgnoreCase);
    }

    private void TryBuildAndAddBucket(List<CleanupBucket> buckets, string targetPath, TargetRule target)
    {
        var bucket = _bucketBuilder.BuildBucket(
            targetPath,
            AppName,
            target.RiskLevel,
            _rule.DefaultAction,
            BuildDescription(target),
            target.Kind);

        if (bucket is not null)
        {
            buckets.Add(bucket);
        }
    }

    private string BuildDescription(TargetRule target)
    {
        if (string.IsNullOrWhiteSpace(_rule.Description))
        {
            return target.Kind;
        }

        return $"{_rule.Description} ({target.Kind})";
    }

    private static bool HasWildcardPrefix(string path)
    {
        return path.StartsWith($"*{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        return relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
    }

    private static string TrimWildcardPrefix(string path)
    {
        if (!HasWildcardPrefix(path))
        {
            return path;
        }

        return path.Substring(2);
    }

    private static string? ResolveRealPrimaryPath(
        IEnumerable<string> targetDirectories,
        IEnumerable<string> hotPaths)
    {
        foreach (var targetDirectory in targetDirectories)
        {
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                continue;
            }

            if (DirectoryExistsSafe(targetDirectory))
            {
                return targetDirectory;
            }
        }

        foreach (var hotPath in hotPaths)
        {
            string? expandedPath = ExpandSafePath(hotPath);
            if (string.IsNullOrWhiteSpace(expandedPath))
            {
                continue;
            }

            if (DirectoryExistsSafe(expandedPath))
            {
                return expandedPath;
            }
        }

        return null;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.EnumerateDirectories(root, "*", _fastEnumOptions);
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }
}

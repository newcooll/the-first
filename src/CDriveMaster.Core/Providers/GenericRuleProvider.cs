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
    private static readonly SemaphoreSlim _probeSemaphore = new(2, 2);
    private static readonly EnumerationOptions _fastEnumOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        RecurseSubdirectories = false
    };

    private readonly CleanupRule _rule;
    private readonly IAppDetector _detector;
    private readonly BucketBuilder _bucketBuilder;

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

                            if (score >= thresholdScore)
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

                                // Stop exploring matched branch to avoid duplicate nested hotspots.
                                continue;
                            }

                            rejectedDirectories.Add(subDir);
                            rejectReasons.Add($"{subDir} -> 评分 {score} 未达阈值 {thresholdScore}");

                            pending.Enqueue((subDir, currentDepth + 1));
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

            foreach (var targetDirectory in targetDirectories)
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

            string? primaryPath = ResolveRealPrimaryPath(targetDirectories, hint.HotPaths);

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
                targetDirectories,
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
                AppTokens = appTokens,
                CacheTokens = cacheTokens,
                MaxDepth = searchHint.MaxDepth,
                ScoreThreshold = 5
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
                Entries: entries.AsReadOnly());
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
            Entries: new List<CleanupEntry> { fallbackEntry }.AsReadOnly());
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
                        long sizeBytes = await CalculateDirectorySizeAsync(subDir, ct);
                        if (sizeBytes >= minSizeBytes)
                        {
                            findings.Add(new FastScanFinding
                            {
                                AppId = rule.AppName,
                                SizeBytes = sizeBytes,
                                Category = "ResidualCache",
                                PrimaryPath = subDir,
                                SourcePath = subDir,
                                IsExactSize = true,
                                DisplaySize = SizeFormatter.Format(sizeBytes),
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
                                OriginalBucket = new CleanupBucket(
                                    BucketId: $"residual:{rule.AppName}:{Guid.NewGuid():N}",
                                    Category: "ResidualCache",
                                    RootPath: subDir,
                                    AppName: rule.AppName,
                                    RiskLevel: RiskLevel.SafeWithPreview,
                                    SuggestedAction: rule.DefaultAction,
                                    Description: $"Residual hotspot - {new DirectoryInfo(subDir).Name}",
                                    EstimatedSizeBytes: sizeBytes,
                                    Entries: new List<CleanupEntry>
                                    {
                                        new CleanupEntry(
                                            Path: subDir,
                                            IsDirectory: true,
                                            SizeBytes: sizeBytes,
                                            LastWriteTimeUtc: DateTime.UtcNow,
                                            Category: "ResidualCache")
                                    }.AsReadOnly())
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

    private static string? ExpandSafePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(rawPath.Replace('/', '\\'));
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
        long totalBytes = 0;
        var pending = new Queue<string>();
        pending.Enqueue(rootPath);
        var stopwatch = Stopwatch.StartNew();

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

        return totalBytes;
    }

    private static string ExpandBaseFolder(string baseFolder)
    {
        if (string.IsNullOrWhiteSpace(baseFolder))
        {
            return string.Empty;
        }

        return Environment.ExpandEnvironmentVariables(baseFolder);
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
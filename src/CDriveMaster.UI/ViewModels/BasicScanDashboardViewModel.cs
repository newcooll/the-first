using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CDriveMaster.Core.Interfaces;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Providers;
using CDriveMaster.Core.Services;
using CDriveMaster.UI.Messages;
using CDriveMaster.UI.Services;

namespace CDriveMaster.UI.ViewModels;

public partial class BasicScanDashboardViewModel : ObservableObject
{
    internal const long ReverseAttributionMinimumBytes = 100L * 1024L * 1024L;
    private readonly LargeFileScanner scanner;
    private readonly RuleCatalog ruleCatalog;
    private readonly AppPresenceDetector appPresenceDetector;
    private readonly ICleanupPipeline cleanupPipeline;
    private readonly AuditLogExporter auditLogExporter;
    private readonly IDialogService dialogService;
    private CancellationTokenSource? cts;
    private CancellationTokenSource? selectionSummaryDebounceCts;
    private readonly List<BasicScanItem> subscribedItems = new();

    public ObservableCollection<BasicScanGroup> ScanGroups { get; } = new();

    public ObservableCollection<FastScanFinding> AppHotspots { get; } = new();

    public ObservableCollection<FastScanFinding> ResidualHotspots { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanFastCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanFullCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCleanSelectedCommand))]
    private bool isBusy;

    [ObservableProperty]
    private string statusText = "准备就绪";

    [ObservableProperty]
    private double progressValue;

    [ObservableProperty]
    private bool isIndeterminate;

    [ObservableProperty]
    private string statsText = "已扫描: 0 个文件夹 / 0 个文件 | 跳过: 0";

    [ObservableProperty]
    private long totalFoundBytes;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCleanSelectedCommand))]
    private long totalSelectedBytes;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCleanSelectedCommand))]
    private int selectedCount;

    public BasicScanDashboardViewModel(
        LargeFileScanner scanner,
        RuleCatalog ruleCatalog,
        AppPresenceDetector appPresenceDetector,
        ICleanupPipeline cleanupPipeline,
        AuditLogExporter auditLogExporter,
        IDialogService dialogService)
    {
        this.scanner = scanner;
        this.ruleCatalog = ruleCatalog;
        this.appPresenceDetector = appPresenceDetector;
        this.cleanupPipeline = cleanupPipeline;
        this.auditLogExporter = auditLogExporter;
        this.dialogService = dialogService;
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanFastAsync()
    {
        await ScanDashboardAsync(isFullScan: false);
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanFullAsync()
    {
        await ScanDashboardAsync(isFullScan: true);
    }

    private async Task ScanDashboardAsync(bool isFullScan)
    {
        cts?.Dispose();
        cts = new CancellationTokenSource();

        IsBusy = true;
        StatusText = isFullScan ? "正在全盘深度扫描，请稍候..." : "正在快速扫描热点目录...";
        IsIndeterminate = isFullScan;
        ProgressValue = 0;

        var progress = new Progress<LargeFileScanner.ScanProgress>(p =>
        {
            StatusText = p.CurrentPath;
            IsIndeterminate = p.IsIndeterminate;
            ProgressValue = p.Percentage;
            StatsText = $"已扫描: {p.ScannedDirs} 个文件夹 / {p.ScannedFiles} 个文件 | 跳过: {p.SkippedCount}";
        });

        try
        {
            var providers = ruleCatalog.GetAllProviders().OfType<GenericRuleProvider>().ToList();
            var rules = providers
                .Select(provider => provider.Rule)
                .ToList();
            var tempBuckets = await Task.Run(() => GetSystemTempBuckets(providers), cts.Token);
            var largeFiles = isFullScan
                ? await scanner.ScanFullAsync(progress, ct: cts.Token)
                : await scanner.ScanFastAsync(progress, ct: cts.Token);
            var reverseAttributedHotspots = BuildReverseAttributedHotspots(largeFiles, rules);

            var groups = BuildGroups(tempBuckets, largeFiles);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ReplaceScanGroups(groups);
                ReplaceAppHotspots(reverseAttributedHotspots);
                ReplaceResidualHotspots(Array.Empty<FastScanFinding>());
            });

            IsIndeterminate = true;
            bool hotspotAnalysisFailed = false;
            bool residualAnalysisFailed = false;

            StatusText = "正在分析应用热点...";
            try
            {
                var hotspots = await ProbeHotspotsAsync(
                    providers,
                    cts.Token,
                    finding => Application.Current.Dispatcher.Invoke(() => MergeAppHotspot(finding)));
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ReplaceAppHotspots(CombineAppHotspots(hotspots, reverseAttributedHotspots));
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                hotspotAnalysisFailed = true;
                Debug.WriteLine($"应用热点分析访问受限: {ex}");
            }
            catch (IOException ex)
            {
                hotspotAnalysisFailed = true;
                Debug.WriteLine($"应用热点分析 I/O 异常: {ex}");
            }
            catch (Exception ex)
            {
                hotspotAnalysisFailed = true;
                Debug.WriteLine($"应用热点分析失败: {ex}");
            }

            StatusText = "正在分析残留痕迹...";
            try
            {
                var residualHotspots = await ProbeResidualHotspotsAsync(
                    providers,
                    cts.Token,
                    finding => Application.Current.Dispatcher.Invoke(() => MergeResidualHotspot(finding)));
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ReplaceResidualHotspots(residualHotspots);
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                residualAnalysisFailed = true;
                Debug.WriteLine($"残留痕迹分析访问受限: {ex}");
            }
            catch (IOException ex)
            {
                residualAnalysisFailed = true;
                Debug.WriteLine($"残留痕迹分析 I/O 异常: {ex}");
            }
            catch (Exception ex)
            {
                residualAnalysisFailed = true;
                Debug.WriteLine($"残留痕迹分析失败: {ex}");
            }

            StatusText = hotspotAnalysisFailed || residualAnalysisFailed
                ? isFullScan
                    ? $"全盘扫描完成，部分热点分析失败，已生成 {ScanGroups.Count} 个分组"
                    : $"快速扫描完成，部分热点分析失败，已生成 {ScanGroups.Count} 个分组"
                : isFullScan
                    ? $"全盘扫描完成，已生成 {ScanGroups.Count} 个分组"
                    : $"快速扫描完成，已生成 {ScanGroups.Count} 个分组";
            IsIndeterminate = false;
            ProgressValue = 100;
        }
        catch (OperationCanceledException)
        {
            StatusText = "扫描已取消";
            IsIndeterminate = false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            StatusText = $"扫描失败: {ex.Message}";
            IsIndeterminate = false;
            await dialogService.ShowErrorAsync("扫描失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
            cts?.Dispose();
            cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteClean))]
    private async Task ExecuteCleanSelectedAsync()
    {
        IsBusy = true;
        StatusText = "正在清理已勾选安全项...";

        try
        {
            var targetsToClean = ScanGroups
                .SelectMany(group => group.Items)
                .Where(item =>
                    item.IsSelected &&
                    item.OriginalBucket is not null &&
                    item.ActionType == BasicScanActionType.CleanSelected)
                .Select(item => item.OriginalBucket!)
                .DistinctBy(bucket => bucket.BucketId)
                .ToList();

            if (targetsToClean.Count == 0)
            {
                StatusText = "未发现可执行的有效安全项";
                await dialogService.ShowInfoAsync("提示", "未发现可执行的有效安全项。");
                return;
            }

            var results = await cleanupPipeline.ExecuteAsync(targetsToClean, apply: true);

            int successCount = results.Sum(result => result.SuccessCount);
            int skippedCount = results
                .SelectMany(result => result.Logs)
                .Count(log => log.Status == ExecutionStatus.Skipped);
            int failedCount = results.Sum(result => result.FailedCount);

            long reclaimedBytes = results
                .Where(result => result.FinalStatus == ExecutionStatus.Success || result.FinalStatus == ExecutionStatus.PartialSuccess)
                .Sum(result => result.ReclaimedSizeBytes);

            await auditLogExporter.ExportAsync("基础扫描聚合", results);

            string summary =
                $"清理完成！{Environment.NewLine}{Environment.NewLine}" +
                $"✅ 成功释放: {CDriveMaster.Core.Utilities.SizeFormatter.Format(reclaimedBytes)}{Environment.NewLine}" +
                $"✅ 成功删除: {successCount} 项{Environment.NewLine}" +
                $"🛡️ 安全跳过: {skippedCount} 项 (文件正被系统或其他软件占用){Environment.NewLine}" +
                (failedCount > 0 ? $"❌ 失败项: {failedCount} 项{Environment.NewLine}" : string.Empty) +
                $"{Environment.NewLine}注：被占用的文件跳过属于安全机制，强删会导致系统崩溃。";

            await dialogService.ShowInfoAsync("清理完成", summary);
            StatusText = "清理完成，正在刷新...";

            await ScanDashboardAsync(isFullScan: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            StatusText = $"清理失败: {ex.Message}";
            await dialogService.ShowErrorAsync("清理失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    private void CancelScan()
    {
        if (cts is null)
        {
            return;
        }

        StatusText = "正在取消扫描...";
        cts.Cancel();
    }

    [RelayCommand]
    private void OpenFolder(BasicScanItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.FullPath))
        {
            return;
        }

        string targetPath = item.FullPath;
        string arguments = item.ActionType == BasicScanActionType.OpenFolder
            ? $"/select,\"{targetPath}\""
            : $"\"{targetPath}\"";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    [RelayCommand]
    private void NavigateToAppCleanup(FastScanFinding? hotspot)
    {
        if (hotspot is null || string.IsNullOrWhiteSpace(hotspot.AppId))
        {
            return;
        }

        WeakReferenceMessenger.Default.Send(new NavigateToAppCleanupMessage(hotspot.AppId));
    }

    [RelayCommand]
    private void ViewDetails(FastScanFinding? finding)
    {
        if (finding is null)
        {
            return;
        }

        var trace = finding.Trace;
        var messageBuilder = new StringBuilder();
        messageBuilder.AppendLine($"应用: {finding.AppName}");
        messageBuilder.AppendLine($"热点状态: {(finding.IsHotspot ? "已达热点阈值" : "未达热点阈值")}");
        messageBuilder.AppendLine($"证据得分: {trace.EvidenceScore}");
        messageBuilder.AppendLine($"命中证据: {FormatTraceEntries(trace.MatchedEvidences)}");
        messageBuilder.AppendLine($"加权过程: {FormatTraceEntries(trace.MatchHistory)}");
        messageBuilder.AppendLine($"候选目录: {FormatTraceEntries(trace.CandidateDirectories)}");
        messageBuilder.AppendLine($"通过验证目录: {FormatTraceEntries(trace.VerifiedDirectories)}");
        messageBuilder.AppendLine($"淘汰目录: {FormatTraceEntries(trace.RejectedDirectories)}");
        messageBuilder.AppendLine($"淘汰原因: {FormatTraceEntries(trace.RejectReasons)}");
        messageBuilder.Append($"最终结论: {trace.FinalDecision}");

        _ = dialogService.ShowInfoAsync("探测链路详情", messageBuilder.ToString());
    }

    private static string FormatTraceEntries(
        IReadOnlyList<string>? entries,
        int maxItems = 12,
        int maxChars = 1600)
    {
        if (entries is null || entries.Count == 0)
        {
            return "无";
        }

        var builder = new StringBuilder();
        int shownCount = 0;
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (shownCount >= maxItems)
            {
                break;
            }

            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            int remainingChars = maxChars - builder.Length;
            if (remainingChars <= 0)
            {
                break;
            }

            string value = entry.Length > remainingChars
                ? entry[..Math.Max(remainingChars - 1, 0)] + "…"
                : entry;

            builder.Append(value);
            shownCount++;

            if (builder.Length >= maxChars)
            {
                break;
            }
        }

        if (shownCount == 0)
        {
            return "无";
        }

        if (shownCount < entries.Count)
        {
            return $"{builder} ……（共 {entries.Count} 项，仅显示前 {shownCount} 项）";
        }

        return builder.ToString();
    }

    [RelayCommand]
    private async Task CleanHotspotAsync(FastScanFinding? finding)
    {
        if (finding is null)
        {
            return;
        }

        if (finding.OriginalBucket is null)
        {
            await dialogService.ShowInfoAsync("提示", "缺少执行上下文，无法清理");
            return;
        }

        if (finding.IsHeuristicMatch)
        {
            bool confirmed = await dialogService.ConfirmAsync(
                "高阶缓存清理确认",
                $"这是一个通过深层特征嗅探发现的缓存目录。{Environment.NewLine}{Environment.NewLine}" +
                "强行清理可能会导致该应用的部分设置被重置，或丢失未保存的本地草稿/下载记录。" +
                $"{Environment.NewLine}{Environment.NewLine}" +
                $"目标路径: {finding.OriginalBucket.RootPath}" +
                $"{Environment.NewLine}{Environment.NewLine}" +
                "确认要彻底删除吗？");

            if (!confirmed)
            {
                return;
            }
        }

        IsBusy = true;
        StatusText = $"正在清理 {finding.AppName} 热点...";
        try
        {
            var results = await cleanupPipeline.ExecuteAsync(new[] { finding.OriginalBucket }, apply: true, CancellationToken.None);
            int successCount = results.Sum(result => result.SuccessCount);
            int skippedCount = results
                .SelectMany(result => result.Logs)
                .Count(log => log.Status == ExecutionStatus.Skipped);
            int failedCount = results.Sum(result => result.FailedCount);
            long reclaimedBytes = results.Sum(result => result.ReclaimedSizeBytes);
            await auditLogExporter.ExportAsync(finding.AppName, results);

            string summary =
                $"清理完成！{Environment.NewLine}{Environment.NewLine}" +
                $"✅ 成功释放: {CDriveMaster.Core.Utilities.SizeFormatter.Format(reclaimedBytes)}{Environment.NewLine}" +
                $"✅ 成功删除: {successCount} 项{Environment.NewLine}" +
                $"🛡️ 安全跳过: {skippedCount} 项 (文件正被系统或其他软件占用){Environment.NewLine}" +
                (failedCount > 0 ? $"❌ 失败项: {failedCount} 项{Environment.NewLine}" : string.Empty) +
                $"{Environment.NewLine}注：被占用的文件跳过属于安全机制，强删会导致系统崩溃。";

            await dialogService.ShowInfoAsync(
                "清理完成",
                summary);

            AppHotspots.Remove(finding);
            ResidualHotspots.Remove(finding);
            RefreshDashboardTotals();
            RecalculateSelectionSummaryImmediate();
            StatusText = $"{finding.AppName} 热点已清理";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            StatusText = $"清理失败: {ex.Message}";
            await dialogService.ShowErrorAsync("清理失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanScan() => !IsBusy;

    private bool CanCancelScan() => IsBusy;

    private bool CanExecuteClean()
    {
        if (IsBusy)
        {
            return false;
        }

        var selectedItems = ScanGroups
            .SelectMany(group => group.Items)
            .Where(item => item.IsSelected)
            .ToList();

        if (selectedItems.Count == 0)
        {
            return false;
        }

        return selectedItems.All(item =>
            item.OriginalBucket is not null &&
            item.ActionType == BasicScanActionType.CleanSelected &&
            item.RiskLevel == RiskLevel.SafeAuto);
    }

    private IReadOnlyList<CleanupBucket> GetSystemTempBuckets(IReadOnlyList<GenericRuleProvider> providers)
    {
        var provider = providers
            .FirstOrDefault(p => string.Equals(p.AppName, "系统临时文件", StringComparison.OrdinalIgnoreCase));

        return provider?.GetBuckets() ?? Array.Empty<CleanupBucket>();
    }

    private async Task<IReadOnlyList<FastScanFinding>> ProbeHotspotsAsync(
        IReadOnlyList<GenericRuleProvider> providers,
        CancellationToken ct,
        Action<FastScanFinding>? onFinding = null)
    {
        var (findings, scoreByRule) = await Task.Run(async () =>
        {
            if (providers.Count == 0)
            {
                return (Array.Empty<FastScanFinding?>(), new Dictionary<string, AppEvidenceScore>(StringComparer.OrdinalIgnoreCase));
            }

            var rules = providers.Select(provider => provider.Rule).ToList();
            var evaluated = await appPresenceDetector.EvaluateAppsAsync(rules);
            if (evaluated.Count == 0)
            {
                return (Array.Empty<FastScanFinding?>(), new Dictionary<string, AppEvidenceScore>(StringComparer.OrdinalIgnoreCase));
            }

            var scoreByRuleLocal = evaluated
                .ToDictionary(score => score.AppId, StringComparer.OrdinalIgnoreCase);

            var allowedRuleNames = evaluated
                .Select(score => score.AppId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (allowedRuleNames.Count == 0)
            {
                return (Array.Empty<FastScanFinding?>(), scoreByRuleLocal);
            }

            var activeProviders = providers
                .Where(provider => allowedRuleNames.Contains(provider.Rule.AppName))
                .OrderByDescending(provider =>
                    scoreByRuleLocal.TryGetValue(provider.Rule.AppName, out var score) ? score.TotalScore : 0)
                .ThenByDescending(provider => provider.Rule.FastScan?.HotPaths.Count ?? 0)
                .ToList();
            if (activeProviders.Count == 0)
            {
                return (Array.Empty<FastScanFinding?>(), scoreByRuleLocal);
            }

            var tasks = activeProviders
                .Select(async provider => new
                {
                    provider.Rule.AppName,
                    Finding = await provider.ProbeAsync(provider.Rule, ct)
                })
                .ToList();

            var localFindings = new List<FastScanFinding?>();
            while (tasks.Count > 0)
            {
                var completedTask = await Task.WhenAny(tasks);
                tasks.Remove(completedTask);

                var completed = await completedTask;
                if (completed.Finding is null)
                {
                    continue;
                }

                var value = completed.Finding;
                if (scoreByRuleLocal.TryGetValue(value.AppId, out var evidenceScore))
                {
                    value = value with
                    {
                        Trace = value.Trace with
                        {
                            EvidenceScore = evidenceScore.TotalScore,
                            MatchedEvidences = new List<string>(evidenceScore.MatchedEvidences)
                        }
                    };
                }

                localFindings.Add(value);
                onFinding?.Invoke(value);
            }

            return (localFindings.ToArray(), scoreByRuleLocal);
        }, ct);

        return findings
            .Where(finding => finding is not null)
            .Select(finding => finding!)
            .OrderByDescending(finding => finding.TotalSizeBytes)
            .ToList();
    }

    private async Task<IReadOnlyList<FastScanFinding>> ProbeResidualHotspotsAsync(
        IReadOnlyList<GenericRuleProvider> providers,
        CancellationToken ct,
        Action<FastScanFinding>? onFinding = null)
    {
        if (providers.Count == 0)
        {
            return Array.Empty<FastScanFinding>();
        }

        var residualProviders = providers
            .Where(provider => provider.Rule.ResidualFingerprints is { Count: > 0 })
            .ToList();
        if (residualProviders.Count == 0)
        {
            return Array.Empty<FastScanFinding>();
        }

        var tasks = residualProviders
            .Select(async provider => await provider.ScanResiduesAsync(provider.Rule, ct))
            .ToList();

        var findings = new List<FastScanFinding>();
        while (tasks.Count > 0)
        {
            var completedTask = await Task.WhenAny(tasks);
            tasks.Remove(completedTask);

            var result = await completedTask;
            foreach (var finding in result)
            {
                findings.Add(finding);
                onFinding?.Invoke(finding);
            }
        }

        return findings
            .OrderByDescending(finding => finding.TotalSizeBytes)
            .ToList();
    }

    internal static IReadOnlyList<FastScanFinding> BuildReverseAttributedHotspots(
        IReadOnlyList<LargeFileItem> largeFiles,
        IReadOnlyList<CleanupRule> rules)
    {
        if (largeFiles.Count == 0 || rules.Count == 0)
        {
            return Array.Empty<FastScanFinding>();
        }

        var aggregatedMatches = new Dictionary<string, ReverseAttributedMatch>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in largeFiles
                     .Where(file => file.SizeBytes >= ReverseAttributionMinimumBytes)
                     .OrderByDescending(file => file.SizeBytes))
        {
            var match = MatchLargeFileToRule(file, rules);
            if (match is null)
            {
                continue;
            }

            if (!aggregatedMatches.TryGetValue(match.Rule.AppName, out var existing))
            {
                aggregatedMatches[match.Rule.AppName] = match;
                continue;
            }

            aggregatedMatches[match.Rule.AppName] = existing.Merge(match);
        }

        return aggregatedMatches.Values
            .Select(BuildReverseAttributedFinding)
            .OrderByDescending(finding => finding.TotalSizeBytes)
            .ToList();
    }

    private IReadOnlyList<BasicScanGroup> BuildGroups(
        IReadOnlyList<CleanupBucket> tempBuckets,
        IReadOnlyList<LargeFileItem> topFiles)
    {
        var tempGroup = new BasicScanGroup
        {
            GroupId = "safe-cleanup",
            Title = "可安全清理",
            Description = "系统临时文件与 %TEMP% 规则结果"
        };

        foreach (var bucket in tempBuckets)
        {
            var recommendation = RecommendationEngine.GenerateRecommendation(
                bucket.RootPath,
                bucket.RiskLevel,
                BasicScanActionType.CleanSelected);

            tempGroup.Items.Add(new BasicScanItem
            {
                Id = BuildTempItemId(bucket),
                Title = bucket.Description,
                Description = bucket.Category,
                FullPath = bucket.RootPath,
                SizeBytes = bucket.EstimatedSizeBytes,
                RiskLevel = bucket.RiskLevel,
                ActionType = BasicScanActionType.CleanSelected,
                IsSelectable = true,
                IsSelected = true,
                OriginalBucket = bucket,
                Recommendation = recommendation
            });
        }

        var largeFileGroup = new BasicScanGroup
        {
            GroupId = "large-file-radar",
            Title = "大文件雷达",
            Description = "Top 20 大文件，默认不勾选，仅供标记和定位"
        };

        foreach (var file in topFiles)
        {
            var recommendation = RecommendationEngine.GenerateRecommendation(
                file.FilePath,
                RiskLevel.SafeWithPreview,
                BasicScanActionType.OpenFolder);

            largeFileGroup.Items.Add(new BasicScanItem
            {
                Id = $"large:{file.FilePath}",
                Title = file.FileName,
                Description = "大文件分析结果",
                FullPath = file.FilePath,
                SizeBytes = file.SizeBytes,
                RiskLevel = RiskLevel.SafeWithPreview,
                ActionType = BasicScanActionType.OpenFolder,
                IsSelectable = true,
                IsSelected = false,
                OriginalBucket = null,
                Recommendation = recommendation
            });
        }

        return new[] { tempGroup, largeFileGroup };
    }

    private static string BuildTempItemId(CleanupBucket bucket) => $"temp:{bucket.BucketId}";

    private void ReplaceAppHotspots(IReadOnlyList<FastScanFinding> hotspots)
    {
        AppHotspots.Clear();
        foreach (var hotspot in hotspots)
        {
            AppHotspots.Add(hotspot);
        }

        RefreshDashboardTotals();
    }

    private void MergeAppHotspot(FastScanFinding hotspot)
    {
        int existingIndex = AppHotspots
            .Select((item, index) => new { item, index })
            .Where(entry => string.Equals(entry.item.AppId, hotspot.AppId, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.index)
            .FirstOrDefault(-1);

        if (existingIndex >= 0)
        {
            AppHotspots[existingIndex] = hotspot;
        }
        else
        {
            AppHotspots.Add(hotspot);
        }

        RefreshDashboardTotals();
    }

    private void ReplaceResidualHotspots(IReadOnlyList<FastScanFinding> hotspots)
    {
        ResidualHotspots.Clear();
        foreach (var hotspot in hotspots)
        {
            ResidualHotspots.Add(hotspot);
        }

        RefreshDashboardTotals();
    }

    private void MergeResidualHotspot(FastScanFinding hotspot)
    {
        int existingIndex = ResidualHotspots
            .Select((item, index) => new { item, index })
            .Where(entry =>
                string.Equals(entry.item.AppId, hotspot.AppId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.item.PrimaryPath, hotspot.PrimaryPath, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.index)
            .FirstOrDefault(-1);

        if (existingIndex >= 0)
        {
            ResidualHotspots[existingIndex] = hotspot;
        }
        else
        {
            ResidualHotspots.Add(hotspot);
        }

        RefreshDashboardTotals();
    }

    private void ReplaceScanGroups(IReadOnlyList<BasicScanGroup> groups)
    {
        UnsubscribeAllItems();
        ScanGroups.Clear();

        foreach (var group in groups)
        {
            ScanGroups.Add(group);
            foreach (var item in group.Items)
            {
                item.PropertyChanged += OnScanItemPropertyChanged;
                subscribedItems.Add(item);
            }
        }

        RefreshDashboardTotals();
        RecalculateSelectionSummaryImmediate();
    }

    private void RefreshDashboardTotals()
    {
        long groupBytes = ScanGroups.SelectMany(group => group.Items).Sum(item => item.SizeBytes);
        long hotspotBytes = AppHotspots
            .Where(item => item.IsHotspot)
            .Sum(item => item.TotalSizeBytes)
            + ResidualHotspots
                .Where(item => item.IsHotspot)
                .Sum(item => item.TotalSizeBytes);
        TotalFoundBytes = groupBytes + hotspotBytes;
    }

    private static IReadOnlyList<FastScanFinding> CombineAppHotspots(
        IReadOnlyList<FastScanFinding> primary,
        IReadOnlyList<FastScanFinding> fallback)
    {
        var merged = fallback.ToDictionary(item => item.AppId, StringComparer.OrdinalIgnoreCase);
        foreach (var item in primary)
        {
            merged[item.AppId] = item;
        }

        return merged.Values
            .OrderByDescending(item => item.TotalSizeBytes)
            .ToList();
    }

    private static ReverseAttributedMatch? MatchLargeFileToRule(
        LargeFileItem file,
        IReadOnlyList<CleanupRule> rules)
    {
        ReverseAttributedMatch? bestMatch = null;
        foreach (var rule in rules)
        {
            if (rule.FastScan is null)
            {
                continue;
            }

            var match = BuildReverseAttributedMatch(file, rule);
            if (match is null)
            {
                continue;
            }

            if (bestMatch is null || match.Score > bestMatch.Score)
            {
                bestMatch = match;
            }
        }

        return bestMatch;
    }

    private static ReverseAttributedMatch? BuildReverseAttributedMatch(LargeFileItem file, CleanupRule rule)
    {
        string normalizedPath = file.FilePath.Replace('/', '\\');
        var explicitRoots = EnumerateRuleRoots(rule)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? explicitRoot = explicitRoots
            .Where(root => normalizedPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();

        var appKeywords = EnumerateAppKeywords(rule).ToArray();
        var cacheKeywords = EnumerateCacheKeywords(rule).ToArray();

        int score = 0;
        bool isExplicit = false;
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            score += 1000 + explicitRoot.Length;
            isExplicit = true;
        }

        bool matchedAppKeyword = appKeywords.Any(keyword =>
            normalizedPath.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        if (matchedAppKeyword)
        {
            score += 120;
        }

        bool matchedCacheKeyword = cacheKeywords.Any(keyword =>
            normalizedPath.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        if (matchedCacheKeyword)
        {
            score += 40;
        }

        if (!isExplicit && !matchedAppKeyword)
        {
            return null;
        }

        if (!isExplicit && !matchedCacheKeyword && file.SizeBytes < 500L * 1024L * 1024L)
        {
            return null;
        }

        string primaryPath = explicitRoot ?? Path.GetDirectoryName(normalizedPath) ?? normalizedPath;
        string finalDecision = isExplicit
            ? "通过大文件反向归因命中规则根路径"
            : "通过大文件反向归因命中应用关键词与缓存关键词";

        return new ReverseAttributedMatch(
            rule,
            score,
            file.SizeBytes,
            primaryPath,
            normalizedPath,
            isExplicit,
            new List<LargeFileItem> { file },
            new ProbeTraceInfo(
                0,
                new List<string>(),
                new List<string> { normalizedPath },
                new List<string> { primaryPath },
                new List<string>(),
                new List<string>(),
                new List<string>
                {
                    $"大文件命中 {file.FileName} ({file.DisplaySize})",
                    finalDecision
                },
                finalDecision));
    }

    private static FastScanFinding BuildReverseAttributedFinding(ReverseAttributedMatch match)
    {
        long threshold = match.Rule.FastScan?.MinSizeThreshold ?? ReverseAttributionMinimumBytes;
        bool isHotspot = match.TotalSizeBytes >= Math.Max(threshold, ReverseAttributionMinimumBytes);
        string finalDecision = isHotspot
            ? "通过深度扫描大文件反向归因，确认存在大体积应用缓存"
            : "通过深度扫描大文件反向归因，疑似存在应用缓存但总体积未达热点阈值";

        var trace = match.Trace with
        {
            CandidateDirectories = match.Files
                .Select(file => file.FilePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            VerifiedDirectories = new List<string> { match.PrimaryPath },
            MatchHistory = match.Files
                .OrderByDescending(file => file.SizeBytes)
                .Select(file => $"大文件反向归因: {file.FilePath} ({file.DisplaySize})")
                .Append(match.Trace.FinalDecision)
                .ToList(),
            FinalDecision = finalDecision
        };

        return new FastScanFinding
        {
            AppId = match.Rule.AppName,
            SizeBytes = match.TotalSizeBytes,
            Category = match.Rule.FastScan?.Category ?? "LargeFileAttribution",
            PrimaryPath = match.PrimaryPath,
            SourcePath = match.Files[0].FilePath,
            IsExactSize = true,
            DisplaySize = CDriveMaster.Core.Utilities.SizeFormatter.Format(match.TotalSizeBytes),
            IsExperimental = match.Rule.FastScan?.IsExperimental ?? false,
            Trace = trace,
            IsHotspot = isHotspot,
            IsHeuristicMatch = !match.IsExplicitRootMatch,
            OriginalBucket = null
        };
    }

    private static IEnumerable<string> EnumerateRuleRoots(CleanupRule rule)
    {
        foreach (var target in rule.Targets)
        {
            string? expanded = ExpandRulePath(target.BaseFolder);
            if (!string.IsNullOrWhiteSpace(expanded))
            {
                yield return expanded;
            }
        }

        if (rule.FastScan?.HotPaths is null)
        {
            yield break;
        }

        foreach (var hotPath in rule.FastScan.HotPaths)
        {
            string? expanded = ExpandRulePath(hotPath);
            if (!string.IsNullOrWhiteSpace(expanded))
            {
                yield return expanded;
            }
        }
    }

    private static IEnumerable<string> EnumerateAppKeywords(CleanupRule rule)
    {
        foreach (var keyword in rule.AppMatchKeywords ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                yield return keyword.Trim();
            }
        }

        if (rule.FastScan?.HeuristicSearchHints is null)
        {
            yield break;
        }

        foreach (var hint in rule.FastScan.HeuristicSearchHints)
        {
            foreach (var token in hint.AppTokens)
            {
                if (!string.IsNullOrWhiteSpace(token))
                {
                    yield return token.Trim();
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateCacheKeywords(CleanupRule rule)
    {
        if (rule.FastScan?.HeuristicSearchHints is not null)
        {
            foreach (var hint in rule.FastScan.HeuristicSearchHints)
            {
                foreach (var token in hint.CacheTokens)
                {
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        yield return token.Trim();
                    }
                }

                foreach (var token in hint.FileMarkersAny)
                {
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        yield return token.Trim();
                    }
                }
            }
        }

        if (rule.FastScan?.SearchHints is null)
        {
            yield break;
        }

        foreach (var hint in rule.FastScan.SearchHints)
        {
            foreach (var token in hint.ChildMarkersAny)
            {
                if (!string.IsNullOrWhiteSpace(token))
                {
                    yield return token.Trim();
                }
            }

            foreach (var token in hint.FileMarkersAny)
            {
                if (!string.IsNullOrWhiteSpace(token))
                {
                    yield return token.Trim();
                }
            }
        }
    }

    private static string? ExpandRulePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        try
        {
            string normalized = rawPath.Replace('/', '\\');
            string expanded = normalized
                .Replace("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase)
                .Replace("%APPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase)
                .Replace("%PROGRAMDATA%", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), StringComparison.OrdinalIgnoreCase);
            expanded = Environment.ExpandEnvironmentVariables(expanded);
            return string.IsNullOrWhiteSpace(expanded) ? null : expanded.TrimEnd('\\');
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private sealed record ReverseAttributedMatch(
        CleanupRule Rule,
        int Score,
        long TotalSizeBytes,
        string PrimaryPath,
        string SourcePath,
        bool IsExplicitRootMatch,
        List<LargeFileItem> Files,
        ProbeTraceInfo Trace)
    {
        public ReverseAttributedMatch Merge(ReverseAttributedMatch other)
        {
            string mergedPrimaryPath = string.Equals(PrimaryPath, other.PrimaryPath, StringComparison.OrdinalIgnoreCase)
                ? PrimaryPath
                : GetCommonAncestor(PrimaryPath, other.PrimaryPath) ?? PrimaryPath;

            var mergedFiles = Files
                .Concat(other.Files)
                .GroupBy(file => file.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(file => file.SizeBytes).First())
                .ToList();

            return this with
            {
                Score = Math.Max(Score, other.Score),
                TotalSizeBytes = TotalSizeBytes + other.TotalSizeBytes,
                PrimaryPath = mergedPrimaryPath,
                SourcePath = Files[0].SizeBytes >= other.Files[0].SizeBytes ? SourcePath : other.SourcePath,
                IsExplicitRootMatch = IsExplicitRootMatch || other.IsExplicitRootMatch,
                Files = mergedFiles
            };
        }
    }

    private static string? GetCommonAncestor(string pathA, string pathB)
    {
        var segmentsA = pathA.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var segmentsB = pathB.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        int commonLength = 0;
        while (commonLength < segmentsA.Length
            && commonLength < segmentsB.Length
            && string.Equals(segmentsA[commonLength], segmentsB[commonLength], StringComparison.OrdinalIgnoreCase))
        {
            commonLength++;
        }

        if (commonLength == 0)
        {
            return null;
        }

        if (commonLength == 1 && segmentsA[0].EndsWith(":", StringComparison.OrdinalIgnoreCase))
        {
            return segmentsA[0] + "\\";
        }

        return string.Join("\\", segmentsA.Take(commonLength));
    }

    private void UnsubscribeAllItems()
    {
        foreach (var item in subscribedItems)
        {
            item.PropertyChanged -= OnScanItemPropertyChanged;
        }

        subscribedItems.Clear();
    }

    private void OnScanItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(BasicScanItem.IsSelected), StringComparison.Ordinal))
        {
            return;
        }

        _ = RecalculateSelectionSummaryDebouncedAsync();
    }

    private void RecalculateSelectionSummaryImmediate()
    {
        var selectedItems = ScanGroups
            .SelectMany(group => group.Items)
            .Where(item => item.IsSelected)
            .ToList();

        TotalSelectedBytes = selectedItems.Sum(item => item.SizeBytes);
        SelectedCount = selectedItems.Count;
        ExecuteCleanSelectedCommand.NotifyCanExecuteChanged();
    }

    private async Task RecalculateSelectionSummaryDebouncedAsync()
    {
        selectionSummaryDebounceCts?.Cancel();
        selectionSummaryDebounceCts?.Dispose();
        selectionSummaryDebounceCts = new CancellationTokenSource();
        var debounceCts = selectionSummaryDebounceCts;

        try
        {
            await Task.Delay(50, debounceCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var snapshot = ScanGroups
            .SelectMany(group => group.Items)
            .ToList();

        var selectedSummary = await Task.Run(() =>
        {
            var selectedItems = snapshot.Where(item => item.IsSelected).ToList();
            return (bytes: selectedItems.Sum(item => item.SizeBytes), count: selectedItems.Count);
        }, debounceCts.Token);

        TotalSelectedBytes = selectedSummary.bytes;
        SelectedCount = selectedSummary.count;
        ExecuteCleanSelectedCommand.NotifyCanExecuteChanged();
    }
}

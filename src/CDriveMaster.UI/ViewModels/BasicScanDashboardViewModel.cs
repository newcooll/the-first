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
            var tempBuckets = await Task.Run(GetSystemTempBuckets, cts.Token);
            var largeFiles = isFullScan
                ? await scanner.ScanFullAsync(progress, ct: cts.Token)
                : await scanner.ScanFastAsync(progress, ct: cts.Token);
            var hotspots = await ProbeHotspotsAsync(cts.Token);
            var residualHotspots = await ProbeResidualHotspotsAsync(cts.Token);

            var groups = BuildGroups(tempBuckets, largeFiles);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ReplaceScanGroups(groups);
                ReplaceAppHotspots(hotspots);
                ReplaceResidualHotspots(residualHotspots);
            });

            StatusText = isFullScan
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
        string message =
            $"应用: {finding.AppName}{Environment.NewLine}" +
            $"热点状态: {(finding.IsHotspot ? "已达热点阈值" : "未达热点阈值")}{Environment.NewLine}" +
            $"证据得分: {trace.EvidenceScore}{Environment.NewLine}" +
            $"命中证据: {(trace.MatchedEvidences.Count == 0 ? "无" : string.Join("; ", trace.MatchedEvidences))}{Environment.NewLine}" +
            $"候选目录: {(trace.CandidateDirectories.Count == 0 ? "无" : string.Join("; ", trace.CandidateDirectories))}{Environment.NewLine}" +
            $"通过验证目录: {(trace.VerifiedDirectories.Count == 0 ? "无" : string.Join("; ", trace.VerifiedDirectories))}{Environment.NewLine}" +
            $"淘汰目录: {(trace.RejectedDirectories.Count == 0 ? "无" : string.Join("; ", trace.RejectedDirectories))}{Environment.NewLine}" +
            $"淘汰原因: {(trace.RejectReasons.Count == 0 ? "无" : string.Join("; ", trace.RejectReasons))}";

        _ = dialogService.ShowInfoAsync("探测链路详情", message);
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

    private IReadOnlyList<CleanupBucket> GetSystemTempBuckets()
    {
        var provider = ruleCatalog.GetAllProviders()
            .FirstOrDefault(p => string.Equals(p.AppName, "系统临时文件", StringComparison.OrdinalIgnoreCase));

        return provider?.GetBuckets() ?? Array.Empty<CleanupBucket>();
    }

    private async Task<IReadOnlyList<FastScanFinding>> ProbeHotspotsAsync(CancellationToken ct)
    {
        var (findings, scoreByRule) = await Task.Run(async () =>
        {
            var providers = ruleCatalog.GetAllProviders().OfType<GenericRuleProvider>().ToList();
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
                .ToList();
            if (activeProviders.Count == 0)
            {
                return (Array.Empty<FastScanFinding?>(), scoreByRuleLocal);
            }

            var tasks = activeProviders
                .Select(provider => provider.ProbeAsync(provider.Rule, ct))
                .ToList();

            var localFindings = await Task.WhenAll(tasks);
            return (localFindings, scoreByRuleLocal);
        }, ct);

        return findings
            .Where(finding => finding is not null)
            .Select(finding =>
            {
                var value = finding!;
                if (!scoreByRule.TryGetValue(value.AppId, out var evidenceScore))
                {
                    return value;
                }

                return value with
                {
                    Trace = value.Trace with
                    {
                        EvidenceScore = evidenceScore.TotalScore,
                        MatchedEvidences = new List<string>(evidenceScore.MatchedEvidences)
                    }
                };
            })
            .OrderByDescending(finding => finding.TotalSizeBytes)
            .ToList();
    }

    private async Task<IReadOnlyList<FastScanFinding>> ProbeResidualHotspotsAsync(CancellationToken ct)
    {
        var providers = ruleCatalog.GetAllProviders().OfType<GenericRuleProvider>().ToList();
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
            .Select(provider => provider.ScanResiduesAsync(provider.Rule, ct))
            .ToList();

        var results = await Task.WhenAll(tasks);
        return results
            .SelectMany(x => x)
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

    private void ReplaceResidualHotspots(IReadOnlyList<FastScanFinding> hotspots)
    {
        ResidualHotspots.Clear();
        foreach (var hotspot in hotspots)
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

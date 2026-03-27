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
using CDriveMaster.Core.Guards;
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
    internal const int AppDeepAttributionTopCount = 120;
    internal const int LargeFileDisplayCount = 20;
    internal const int CleanupExecutionBatchSize = 64;
    private static readonly HashSet<string> UnsafeCleanupExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".sys", ".drv", ".ocx", ".cpl", ".scr", ".com", ".msi", ".msp"
    };
    private readonly LargeFileScanner scanner;
    private readonly RuleCatalog ruleCatalog;
    private readonly AppPresenceDetector appPresenceDetector;
    private readonly ICleanupPipeline cleanupPipeline;
    private readonly AuditLogExporter auditLogExporter;
    private readonly IDialogService dialogService;
    private readonly IPreviewDialogService previewService;
    private CancellationTokenSource? cts;
    private CancellationTokenSource? selectionSummaryDebounceCts;
    private readonly List<BasicScanItem> subscribedItems = new();

    public ObservableCollection<BasicScanGroup> ScanGroups { get; } = new();

    public ObservableCollection<FastScanFinding> AppHotspots { get; } = new();

    public ObservableCollection<FastScanFinding> ResidualHotspots { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanFastCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanFullCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanAppDeepCommand))]
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
    private string cleanupProgressText = string.Empty;

    [ObservableProperty]
    private string cleanupCurrentPathText = string.Empty;

    [ObservableProperty]
    private string cleanupStageText = string.Empty;

    [ObservableProperty]
    private string cleanupSummaryText = string.Empty;

    [ObservableProperty]
    private string cleanupBatchText = string.Empty;

    [ObservableProperty]
    private bool isCleanupVisualVisible;

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
        IDialogService dialogService,
        IPreviewDialogService previewService)
    {
        this.scanner = scanner;
        this.ruleCatalog = ruleCatalog;
        this.appPresenceDetector = appPresenceDetector;
        this.cleanupPipeline = cleanupPipeline;
        this.auditLogExporter = auditLogExporter;
        this.dialogService = dialogService;
        this.previewService = previewService;
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanFastAsync()
    {
        await ScanDashboardAsync(BasicScanMode.Fast);
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanFullAsync()
    {
        await ScanDashboardAsync(BasicScanMode.FullDisk);
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAppDeepAsync()
    {
        await ScanDashboardAsync(BasicScanMode.AppDeep);
    }

    private async Task ScanDashboardAsync(BasicScanMode scanMode)
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = new CancellationTokenSource();
        var scanCts = cts;

        IsBusy = true;
        ResetCleanupExecutionVisuals(clearAll: true);
        StatusText = scanMode switch
        {
            BasicScanMode.Fast => "正在快速扫描系统临时目录...",
            BasicScanMode.FullDisk => "正在全盘深度扫描，请稍候...",
            BasicScanMode.AppDeep => "正在深度扫描应用缓存，请稍候...",
            _ => "正在扫描..."
        };
        IsIndeterminate = scanMode != BasicScanMode.Fast;
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
            var targetedFullScanRoots = BuildTargetedFullScanRoots(rules);
            var tempBuckets = await Task.Run(() => GetSystemTempBuckets(providers), cts.Token);
            var largeFiles = scanMode switch
            {
                BasicScanMode.Fast => await scanner.ScanFastAsync(progress, topCount: LargeFileDisplayCount, ct: cts.Token),
                BasicScanMode.FullDisk => await scanner.ScanFullAsync(progress, ct: cts.Token),
                BasicScanMode.AppDeep => targetedFullScanRoots.Count > 0
                    ? await scanner.ScanFullAsync(progress, targetedFullScanRoots, topCount: AppDeepAttributionTopCount, ct: cts.Token)
                    : await scanner.ScanFullAsync(progress, ct: cts.Token),
                _ => new List<LargeFileItem>()
            };
            var reverseAttributedHotspots = scanMode == BasicScanMode.AppDeep
                ? BuildReverseAttributedHotspots(largeFiles, rules)
                : Array.Empty<FastScanFinding>();

            var groups = BuildGroups(tempBuckets, largeFiles, scanMode);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ReplaceScanGroups(groups);
                ReplaceAppHotspots(reverseAttributedHotspots);
                ReplaceResidualHotspots(Array.Empty<FastScanFinding>());
            });

            if (scanMode == BasicScanMode.Fast)
            {
                StatusText = "快速扫描完成，已更新系统临时缓存结果";
                IsIndeterminate = false;
                ProgressValue = 100;
                return;
            }

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
                ? $"{GetScanModeLabel(scanMode)}完成，部分热点分析失败，已生成 {ScanGroups.Count} 个分组"
                : $"{GetScanModeLabel(scanMode)}完成，已生成 {ScanGroups.Count} 个分组";
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
            if (ReferenceEquals(cts, scanCts))
            {
                cts?.Dispose();
                cts = null;
            }
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

            var results = await PreviewAndExecuteBucketsAsync(
                targetsToClean,
                "安全项清理预览",
                "已勾选安全项");
            if (results is null || results.Count == 0)
            {
                return;
            }

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

            await ScanDashboardAsync(BasicScanMode.Fast);
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
            var results = await PreviewAndExecuteBucketsAsync(
                new[] { finding.OriginalBucket },
                $"{finding.AppName} 热点清理预览",
                $"{finding.AppName} 热点",
                CancellationToken.None,
                useTrustedPreviewEntries: CanUseTrustedPreviewEntries(finding.OriginalBucket));
            if (results is null || results.Count == 0)
            {
                return;
            }

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

            bool fullyCleaned = finding.OriginalBucket.Entries.Count > 0 &&
                results.Sum(result => result.SuccessCount) == finding.OriginalBucket.Entries.Count &&
                results.SelectMany(result => result.Logs).All(log => log.Status == ExecutionStatus.Success);

            if (fullyCleaned)
            {
                AppHotspots.Remove(finding);
                ResidualHotspots.Remove(finding);
            }

            RefreshDashboardTotals();
            RecalculateSelectionSummaryImmediate();
            StatusText = fullyCleaned
                ? $"{finding.AppName} 热点已清理"
                : $"{finding.AppName} 热点已部分清理";
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

    internal async Task<IReadOnlyList<BucketResult>?> PreviewAndExecuteBucketsAsync(
        IReadOnlyList<CleanupBucket> targetBuckets,
        string previewTitle,
        string targetLabel,
        CancellationToken cancellationToken = default,
        bool useTrustedPreviewEntries = false)
    {
        var previewCandidates = targetBuckets
            .SelectMany(bucket => bucket.Entries.Select(entry => new PreviewEntryContext(bucket, entry)))
            .GroupBy(item => item.Entry.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        int candidateCount = previewCandidates.Count;
        long candidateBytes = previewCandidates.Sum(item => item.Entry.SizeBytes);

        StatusText = $"正在准备 {targetLabel} 清理清单...";
        CleanupStageText = "准备清单";
        CleanupSummaryText = $"正在校验 {candidateCount} 个候选条目，约 {CDriveMaster.Core.Utilities.SizeFormatter.Format(candidateBytes)}";
        CleanupBatchText = "正在执行边界校验与安全过滤";
        CleanupProgressText = "正在校验规则边界并生成预览...";
        CleanupCurrentPathText = string.Empty;
        IsCleanupVisualVisible = true;
        IsIndeterminate = false;
        ProgressValue = 0;

        IReadOnlyList<CleanupEntry> previewableEntries;
        int blockedCount;

        if (useTrustedPreviewEntries)
        {
            previewableEntries = previewCandidates
                .Select(item => item.Entry)
                .ToList()
                .AsReadOnly();
            blockedCount = 0;
            ProgressValue = 100;
            CleanupStageText = "等待确认";
            CleanupSummaryText =
                $"已加载 {previewableEntries.Count} 个热点清理条目，预计释放 {CDriveMaster.Core.Utilities.SizeFormatter.Format(candidateBytes)}";
            CleanupBatchText = "热点卡片使用精确文件清单，已跳过预览慢校验";
            CleanupProgressText = $"预览已就绪 {previewableEntries.Count}/{candidateCount}";
        }
        else
        {
            var previewProgress = new Progress<PreviewPreparationProgress>(progress =>
            {
                ProgressValue = progress.TotalCount <= 0
                    ? 0
                    : Math.Clamp((double)progress.ProcessedCount / progress.TotalCount * 100d, 0d, 100d);
                CleanupSummaryText =
                    $"已校验 {progress.ProcessedCount}/{progress.TotalCount} 个条目，允许 {progress.PreviewableCount} 个，阻断 {progress.BlockedCount} 个";
                CleanupBatchText = string.IsNullOrWhiteSpace(progress.CurrentAppName)
                    ? "正在执行边界校验与安全过滤"
                    : $"当前应用: {progress.CurrentAppName}";
                CleanupProgressText =
                    $"预览校验 {progress.ProcessedCount}/{progress.TotalCount} | 可清理 {progress.PreviewableCount} | 阻断 {progress.BlockedCount}";
                CleanupCurrentPathText = string.IsNullOrWhiteSpace(progress.CurrentPath)
                    ? string.Empty
                    : $"当前校验: {progress.CurrentPath}";
            });

            var previewPlan = await BuildPreviewPlanAsync(previewCandidates, previewProgress, cancellationToken);
            previewableEntries = previewPlan.PreviewableEntries;
            blockedCount = previewPlan.BlockedCount;
        }

        long previewBytes = previewableEntries.Sum(entry => entry.SizeBytes);
        string summary = BuildCleanupPreviewSummary(targetLabel, previewableEntries.Count, previewBytes, blockedCount);

        if (previewableEntries.Count == 0)
        {
            StatusText = $"{targetLabel}没有可安全执行的清理条目";
            CleanupStageText = "无可执行条目";
            CleanupSummaryText = $"已校验 {candidateCount} 个条目，没有通过边界校验的安全清理项";
            CleanupBatchText = blockedCount > 0
                ? $"已提前阻断 {blockedCount} 个条目"
                : string.Empty;
            CleanupProgressText = "预览生成完成";
            CleanupCurrentPathText = string.Empty;
            await dialogService.ShowInfoAsync("清理预览", summary);
            return null;
        }

        var bucketByPath = previewCandidates
            .ToDictionary(item => item.Entry.Path, item => item.Bucket, StringComparer.OrdinalIgnoreCase);

        var entryByPath = previewCandidates
            .ToDictionary(item => item.Entry.Path, item => item.Entry, StringComparer.OrdinalIgnoreCase);

        CleanupStageText = "等待确认";
        CleanupSummaryText =
            $"已生成 {previewableEntries.Count} 个可清理条目，预计释放 {CDriveMaster.Core.Utilities.SizeFormatter.Format(previewBytes)}";
        CleanupBatchText = blockedCount > 0
            ? $"已提前阻断 {blockedCount} 个条目，请确认要执行的内容"
            : "预览已生成，请确认要执行的内容";
        CleanupProgressText = "预览生成完成，等待确认";
        CleanupCurrentPathText = string.Empty;

        var selectedEntries = (await previewService.ShowPreviewAsync(previewTitle, previewableEntries, summary))
            .ToList();
        if (selectedEntries.Count == 0)
        {
            StatusText = "已取消清理";
            CleanupStageText = "已取消";
            CleanupSummaryText = "用户未确认执行，本次未删除任何文件";
            CleanupBatchText = string.Empty;
            ResetCleanupExecutionVisuals();
            return null;
        }

        long selectedBytes = selectedEntries.Sum(entry => entry.SizeBytes);
        StatusText = "正在按预览清单分批执行清理...";
        ProgressValue = 0;
        IsIndeterminate = false;
        var workItems = selectedEntries
            .Where(entry => bucketByPath.ContainsKey(entry.Path) && entryByPath.ContainsKey(entry.Path))
            .Select(entry => new CleanupExecutionWorkItem(
                bucketByPath[entry.Path],
                entryByPath[entry.Path]))
            .ToList();

        if (workItems.Count == 0)
        {
            StatusText = "已取消清理";
            ResetCleanupExecutionVisuals();
            return null;
        }

        var workBatches = workItems
            .GroupBy(item => item.Bucket.BucketId, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group
                .Chunk(CleanupExecutionBatchSize)
                .Select(chunk => new CleanupExecutionBatch(
                    group.First().Bucket,
                    chunk.Select(item => item.Entry).ToList().AsReadOnly())))
            .ToList();

        CleanupStageText = "分批执行";
        CleanupSummaryText =
            $"已选择 {selectedEntries.Count} 个条目，预计释放 {CDriveMaster.Core.Utilities.SizeFormatter.Format(selectedBytes)}，共 {workBatches.Count} 批";
        UpdateCleanupExecutionVisuals(
            processedCount: 0,
            totalCount: workItems.Count,
            successCount: 0,
            skippedCount: 0,
            blockedCount: 0,
            failedCount: 0,
            currentPath: workItems[0].Entry.Path,
            currentBatchNumber: workBatches.Count > 0 ? 1 : 0,
            totalBatchCount: workBatches.Count,
            currentBatchEntryCount: workBatches.Count > 0 ? workBatches[0].Entries.Count : 0);
        await Task.Yield();

        try
        {
            var entryResults = new List<BucketResult>(workItems.Count);
            int processedCount = 0;
            int successCount = 0;
            int skippedCount = 0;
            int blockedCountExecution = 0;
            int failedCount = 0;

            for (int batchIndex = 0; batchIndex < workBatches.Count; batchIndex++)
            {
                var batch = workBatches[batchIndex];
                var result = await Task.Run(
                    () => cleanupPipeline.ExecuteEntries(batch.Bucket, batch.Entries, apply: true),
                    cancellationToken);
                entryResults.Add(result);

                processedCount += result.Logs.Count;
                successCount += result.Logs.Count(log => log.Status == ExecutionStatus.Success);
                skippedCount += result.Logs.Count(log => log.Status == ExecutionStatus.Skipped);
                blockedCountExecution += result.Logs.Count(log => log.Status == ExecutionStatus.Blocked);
                failedCount += result.Logs.Count(log => log.Status == ExecutionStatus.Failed);

                string? nextPath = batchIndex + 1 < workBatches.Count
                    ? workBatches[batchIndex + 1].Entries[0].Path
                    : null;

                UpdateCleanupExecutionVisuals(
                    processedCount,
                    workItems.Count,
                    successCount,
                    skippedCount,
                    blockedCountExecution,
                    failedCount,
                    nextPath,
                    currentBatchNumber: Math.Min(batchIndex + 2, workBatches.Count),
                    totalBatchCount: workBatches.Count,
                    currentBatchEntryCount: batchIndex + 1 < workBatches.Count
                        ? workBatches[batchIndex + 1].Entries.Count
                        : 0);
            }

            CleanupStageText = "清理完成";
            CleanupBatchText = $"全部 {workBatches.Count} 批已执行完成";
            CleanupCurrentPathText = string.Empty;
            return AggregateBucketResults(targetBuckets, entryResults);
        }
        finally
        {
            IsIndeterminate = false;
        }
    }

    private static List<CleanupEntry> CollectPreviewableEntries(
        IReadOnlyList<CleanupBucket> targetBuckets,
        IReadOnlyList<BucketResult> dryRunResults)
    {
        var allowedPaths = dryRunResults
            .SelectMany(result => result.Logs)
            .Where(log => log.Status == ExecutionStatus.Skipped)
            .Select(log => log.TargetPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return targetBuckets
            .SelectMany(bucket => bucket.Entries)
            .Where(entry => allowedPaths.Contains(entry.Path))
            .DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static bool CanUseTrustedPreviewEntries(CleanupBucket bucket)
    {
        if (bucket.Entries.Count == 0 || bucket.Entries.Any(entry => entry.IsDirectory))
        {
            return false;
        }

        var entryPaths = bucket.Entries
            .Select(entry => NormalizePathForComparison(entry.Path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (entryPaths.Length == 0)
        {
            return false;
        }

        var allowedRoots = (bucket.AllowedRoots ?? Array.Empty<string>())
            .Select(NormalizePathForComparison)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (allowedRoots.Length != entryPaths.Length)
        {
            return false;
        }

        return allowedRoots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(entryPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizePathForComparison(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static Task<PreviewPreparationPlan> BuildPreviewPlanAsync(
        IReadOnlyList<PreviewEntryContext> candidates,
        IProgress<PreviewPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var guard = new PreflightGuard();
            var previewableEntries = new List<CleanupEntry>(candidates.Count);
            int blockedCount = 0;
            int processedCount = 0;

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var check = guard.CheckPathForPreview(
                    candidate.Entry.Path,
                    candidate.Entry.IsDirectory,
                    candidate.Bucket.AllowedRoots ?? new[] { candidate.Bucket.RootPath });

                if (check.Passed)
                {
                    previewableEntries.Add(candidate.Entry);
                }
                else
                {
                    blockedCount++;
                }

                processedCount++;
                if (processedCount == 1
                    || processedCount == candidates.Count
                    || processedCount % CleanupExecutionBatchSize == 0)
                {
                    progress?.Report(new PreviewPreparationProgress(
                        ProcessedCount: processedCount,
                        TotalCount: candidates.Count,
                        PreviewableCount: previewableEntries.Count,
                        BlockedCount: blockedCount,
                        CurrentPath: candidate.Entry.Path,
                        CurrentAppName: candidate.Bucket.AppName));
                }
            }

            return new PreviewPreparationPlan(previewableEntries.AsReadOnly(), blockedCount);
        }, cancellationToken);
    }

    private static string BuildCleanupPreviewSummary(
        string targetLabel,
        int previewableCount,
        long previewBytes,
        int blockedCount)
    {
        var builder = new StringBuilder();
        builder.Append($"本次共识别出 {previewableCount} 个可执行清理条目");
        if (previewableCount > 0)
        {
            builder.Append($"，预计可释放 {CDriveMaster.Core.Utilities.SizeFormatter.Format(previewBytes)}。");
        }
        else
        {
            builder.Append("。");
        }

        if (blockedCount > 0)
        {
            builder.Append($" 已提前阻断 {blockedCount} 个条目，它们超出了规则边界或命中了系统保护目录。");
        }

        builder.Append($" 下面展示的是当前确认可删的 {targetLabel}；执行阶段如果遇到占用文件，仍会自动跳过并继续处理。");
        return builder.ToString();
    }

    private void ResetCleanupExecutionVisuals(bool clearAll = false)
    {
        CleanupProgressText = string.Empty;
        CleanupCurrentPathText = string.Empty;
        if (clearAll || string.IsNullOrWhiteSpace(CleanupStageText))
        {
            CleanupStageText = string.Empty;
            CleanupSummaryText = string.Empty;
            CleanupBatchText = string.Empty;
            IsCleanupVisualVisible = false;
        }
    }

    private void UpdateCleanupExecutionVisuals(
        int processedCount,
        int totalCount,
        int successCount,
        int skippedCount,
        int blockedCount,
        int failedCount,
        string? currentPath,
        int currentBatchNumber,
        int totalBatchCount,
        int currentBatchEntryCount)
    {
        ProgressValue = totalCount <= 0
            ? 0
            : Math.Clamp((double)processedCount / totalCount * 100d, 0d, 100d);
        CleanupBatchText = totalBatchCount <= 0
            ? string.Empty
            : currentBatchNumber > totalBatchCount
                ? $"全部 {totalBatchCount} 批已执行完成"
                : $"当前批次 {currentBatchNumber}/{totalBatchCount} | 本批 {currentBatchEntryCount} 项 | 每批最多 {CleanupExecutionBatchSize} 项";
        CleanupProgressText =
            $"执行进度 {processedCount}/{totalCount} | 成功 {successCount} | 跳过 {skippedCount} | 阻断 {blockedCount} | 失败 {failedCount}";
        CleanupCurrentPathText = string.IsNullOrWhiteSpace(currentPath)
            ? string.Empty
            : $"当前处理: {currentPath}";
    }

    private static IReadOnlyList<BucketResult> AggregateBucketResults(
        IReadOnlyList<CleanupBucket> targetBuckets,
        IReadOnlyList<BucketResult> entryResults)
    {
        var resultsByBucket = entryResults
            .GroupBy(result => result.Bucket.BucketId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var aggregated = new List<BucketResult>();
        foreach (var bucket in targetBuckets)
        {
            if (!resultsByBucket.TryGetValue(bucket.BucketId, out var bucketResults) || bucketResults.Count == 0)
            {
                continue;
            }

            var logs = bucketResults
                .SelectMany(result => result.Logs)
                .ToList();

            aggregated.Add(new BucketResult(
                Bucket: bucket,
                FinalStatus: AuditAggregator.CalculateBucketStatus(logs),
                ReclaimedSizeBytes: logs
                    .Where(log => log.Status == ExecutionStatus.Success)
                    .Sum(log => log.TargetSizeBytes),
                SuccessCount: logs.Count(log => log.Status == ExecutionStatus.Success),
                FailedCount: logs.Count(log => log.Status == ExecutionStatus.Failed),
                BlockedCount: logs.Count(log => log.Status == ExecutionStatus.Blocked),
                Logs: logs));
        }

        return aggregated.AsReadOnly();
    }

    private sealed record CleanupExecutionWorkItem(CleanupBucket Bucket, CleanupEntry Entry);
    private sealed record CleanupExecutionBatch(CleanupBucket Bucket, IReadOnlyList<CleanupEntry> Entries);
    private sealed record PreviewEntryContext(CleanupBucket Bucket, CleanupEntry Entry);
    private sealed record PreviewPreparationProgress(
        int ProcessedCount,
        int TotalCount,
        int PreviewableCount,
        int BlockedCount,
        string CurrentPath,
        string CurrentAppName);
    private sealed record PreviewPreparationPlan(IReadOnlyList<CleanupEntry> PreviewableEntries, int BlockedCount);

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
        Action<FastScanFinding>? onFinding = null,
        bool seedOnly = false)
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
                    Finding = seedOnly
                        ? await provider.ProbeSeedOnlyAsync(provider.Rule, ct)
                        : await provider.ProbeAsync(provider.Rule, ct)
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
                     .Where(file => IsSafeCleanupFile(file.FilePath))
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
        IReadOnlyList<LargeFileItem> topFiles,
        BasicScanMode scanMode)
    {
        var groups = new List<BasicScanGroup>();

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

        groups.Add(tempGroup);

        if (topFiles.Count == 0 || scanMode == BasicScanMode.AppDeep)
        {
            return groups;
        }

        var largeFileGroup = new BasicScanGroup
        {
            GroupId = "large-file-radar",
            Title = scanMode == BasicScanMode.AppDeep ? "应用缓存深度雷达" : "大文件雷达",
            Description = scanMode == BasicScanMode.AppDeep
                ? "规则命中的应用缓存目录内 Top 20 大文件，默认不勾选，仅供定位"
                : "Top 20 大文件，默认不勾选，仅供标记和定位"
        };

        foreach (var file in topFiles.Take(LargeFileDisplayCount))
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

        groups.Add(largeFileGroup);
        return groups;
    }

    internal static IReadOnlyList<string> BuildTargetedFullScanRoots(IReadOnlyList<CleanupRule> rules)
    {
        var prioritizedRoots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            foreach (var root in EnumerateRuleRoots(rule))
            {
                AddTargetedRoot(prioritizedRoots, root, priority: 0);
            }

            foreach (var root in EnumerateRuleAnchorRoots(rule))
            {
                AddTargetedRoot(prioritizedRoots, root, priority: 1);
            }

            foreach (var root in DiscoverRuleRootsFromSearchParents(rule)
                         )
            {
                AddTargetedRoot(prioritizedRoots, root, priority: 2);
            }
        }

        return prioritizedRoots
            .OrderBy(entry => entry.Value)
            .ThenBy(entry => entry.Key.Length)
            .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Key)
            .Aggregate(
                new List<string>(),
                (selected, path) =>
                {
                    if (selected.Any(existing => IsSameOrAncestorPath(existing, path)))
                    {
                        return selected;
                    }

                    selected.RemoveAll(existing => IsAncestorPath(path, existing));
                    selected.Add(path);

                    return selected;
                })
            .ToList();
    }

    private static string BuildTempItemId(CleanupBucket bucket) => $"temp:{bucket.BucketId}";

    private void ReplaceAppHotspots(IReadOnlyList<FastScanFinding> hotspots)
    {
        var aggregatedHotspots = AggregateFindingsByApp(hotspots);
        AppHotspots.Clear();
        foreach (var hotspot in aggregatedHotspots)
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
            AppHotspots[existingIndex] = SelectPreferredAppHotspot(AppHotspots[existingIndex], hotspot);
        }
        else
        {
            AppHotspots.Add(hotspot);
        }

        RefreshDashboardTotals();
    }

    private void ReplaceResidualHotspots(IReadOnlyList<FastScanFinding> hotspots)
    {
        var aggregatedHotspots = AggregateFindingsByApp(hotspots);
        ResidualHotspots.Clear();
        foreach (var hotspot in aggregatedHotspots)
        {
            ResidualHotspots.Add(hotspot);
        }

        RefreshDashboardTotals();
    }

    private void MergeResidualHotspot(FastScanFinding hotspot)
    {
        int existingIndex = ResidualHotspots
            .Select((item, index) => new { item, index })
            .Where(entry => string.Equals(entry.item.AppId, hotspot.AppId, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.index)
            .FirstOrDefault(-1);

        if (existingIndex >= 0)
        {
            ResidualHotspots[existingIndex] = MergeFindings(ResidualHotspots[existingIndex], hotspot);
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

    internal static IReadOnlyList<FastScanFinding> CombineAppHotspots(
        IReadOnlyList<FastScanFinding> primary,
        IReadOnlyList<FastScanFinding> fallback)
    {
        var merged = AggregateFindingsByApp(fallback)
            .ToDictionary(item => item.AppId, StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in AggregateFindingsByApp(primary))
        {
            if (!merged.TryGetValue(candidate.AppId, out var existing))
            {
                merged[candidate.AppId] = candidate;
                continue;
            }

            merged[candidate.AppId] = SelectPreferredAppHotspot(existing, candidate);
        }

        return merged.Values
            .OrderByDescending(item => item.TotalSizeBytes)
            .ToList();
    }

    private static FastScanFinding SelectPreferredAppHotspot(FastScanFinding existing, FastScanFinding candidate)
    {
        int existingPriority = GetAppHotspotPriority(existing);
        int candidatePriority = GetAppHotspotPriority(candidate);
        if (candidatePriority != existingPriority)
        {
            return candidatePriority > existingPriority ? candidate : existing;
        }

        if (candidate.TotalSizeBytes != existing.TotalSizeBytes)
        {
            return candidate.TotalSizeBytes > existing.TotalSizeBytes ? candidate : existing;
        }

        if (candidate.IsExactSize != existing.IsExactSize)
        {
            return candidate.IsExactSize ? candidate : existing;
        }

        if (candidate.IsHeuristicMatch != existing.IsHeuristicMatch)
        {
            return candidate.IsHeuristicMatch ? existing : candidate;
        }

        return candidate;
    }

    private static int GetAppHotspotPriority(FastScanFinding finding)
    {
        int priority = 0;
        if (finding.IsHotspot)
        {
            priority += 100;
        }

        if (finding.IsExactSize)
        {
            priority += 10;
        }

        if (!finding.IsHeuristicMatch)
        {
            priority += 5;
        }

        return priority;
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
        string normalizedPathToken = NormalizePathToken(normalizedPath);
        var explicitRoots = EnumerateRuleRoots(rule)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var anchorRoots = EnumerateRuleAnchorRoots(rule)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var searchParents = EnumerateRuleSearchParents(rule)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? explicitRoot = explicitRoots
            .Where(root => normalizedPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();
        string? anchorRoot = anchorRoots
            .Where(root => normalizedPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();
        string? parentKeywordAnchor = searchParents
            .Select(parent => TryResolveParentKeywordAnchorRoot(normalizedPath, parent, rule))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .OrderByDescending(path => path!.Length)
            .FirstOrDefault();

        var appKeywords = EnumerateAppKeywords(rule).ToArray();
        var cacheKeywords = EnumerateCacheKeywords(rule).ToArray();
        var residualKeywords = EnumerateResidualKeywords(rule).ToArray();
        var pathSegments = EnumeratePathSegments(normalizedPath).ToArray();
        var normalizedSegments = pathSegments
            .Select(NormalizePathToken)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();

        int score = 0;
        bool isExplicit = false;
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            score += 1000 + explicitRoot.Length;
            isExplicit = true;
        }
        else if (!string.IsNullOrWhiteSpace(anchorRoot))
        {
            score += 400 + anchorRoot.Length;
        }
        else if (!string.IsNullOrWhiteSpace(parentKeywordAnchor))
        {
            score += 300 + parentKeywordAnchor.Length;
        }

        bool matchedAppKeyword = appKeywords.Any(keyword => MatchesNormalizedKeyword(normalizedPathToken, normalizedSegments, keyword));
        if (matchedAppKeyword)
        {
            score += 120;
        }

        bool matchedCacheKeyword = cacheKeywords.Any(keyword => MatchesNormalizedKeyword(normalizedPathToken, normalizedSegments, keyword));
        if (matchedCacheKeyword)
        {
            score += 40;
        }

        bool matchedResidualKeyword = residualKeywords.Any(keyword => MatchesNormalizedKeyword(normalizedPathToken, normalizedSegments, keyword));
        if (matchedResidualKeyword)
        {
            score += 90;
        }

        if (!isExplicit
            && string.IsNullOrWhiteSpace(anchorRoot)
            && string.IsNullOrWhiteSpace(parentKeywordAnchor)
            && !matchedAppKeyword
            && !matchedResidualKeyword)
        {
            return null;
        }

        if (!isExplicit
            && string.IsNullOrWhiteSpace(anchorRoot)
            && string.IsNullOrWhiteSpace(parentKeywordAnchor)
            && !matchedCacheKeyword
            && !matchedResidualKeyword
            && file.SizeBytes < 500L * 1024L * 1024L)
        {
            return null;
        }

        string primaryPath = explicitRoot
            ?? anchorRoot
            ?? parentKeywordAnchor
            ?? Path.GetDirectoryName(normalizedPath)
            ?? normalizedPath;
        string finalDecision = isExplicit
            ? "通过大文件反向归因命中规则根路径"
            : !string.IsNullOrWhiteSpace(anchorRoot)
                ? "通过大文件反向归因命中应用锚点目录"
                : !string.IsNullOrWhiteSpace(parentKeywordAnchor)
                    ? "通过大文件反向归因命中规则父目录下的应用路径"
                : matchedResidualKeyword
                    ? "通过大文件反向归因命中路径关键词"
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
            OriginalBucket = BuildReverseAttributedOriginalBucket(match)
        };
    }

    private static CleanupBucket? BuildReverseAttributedOriginalBucket(ReverseAttributedMatch match)
    {
        var entries = match.Files
            .Where(file => !string.IsNullOrWhiteSpace(file.FilePath))
            .Where(file => IsSafeCleanupFile(file.FilePath))
            .Select(file => new CleanupEntry(
                Path: file.FilePath,
                IsDirectory: false,
                SizeBytes: Math.Max(0, file.SizeBytes),
                LastWriteTimeUtc: file.LastWriteTime.ToUniversalTime(),
                Category: match.Rule.FastScan?.Category ?? "LargeFileAttribution"))
            .DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (entries.Count == 0)
        {
            return null;
        }

        string rootPath = match.PrimaryPath;
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            rootPath = Path.GetDirectoryName(entries[0].Path) ?? entries[0].Path;
        }

        return new CleanupBucket(
            BucketId: $"reverse-attribution:{match.Rule.AppName}:{Guid.NewGuid():N}",
            Category: match.Rule.FastScan?.Category ?? "LargeFileAttribution",
            RootPath: rootPath,
            AppName: match.Rule.AppName,
            RiskLevel: RiskLevel.SafeWithPreview,
            SuggestedAction: match.Rule.DefaultAction,
            Description: "Reverse-attributed hotspot candidate",
            EstimatedSizeBytes: entries.Sum(entry => entry.SizeBytes),
            Entries: entries.AsReadOnly(),
            AllowedRoots: entries
                .Select(entry => entry.Path)
                .ToArray());
    }

    internal static IReadOnlyList<FastScanFinding> AggregateFindingsByApp(IEnumerable<FastScanFinding> findings)
    {
        var merged = new Dictionary<string, FastScanFinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in findings)
        {
            if (!merged.TryGetValue(item.AppId, out var existing))
            {
                merged[item.AppId] = item;
                continue;
            }

            merged[item.AppId] = MergeFindings(existing, item);
        }

        return merged.Values
            .OrderByDescending(item => item.TotalSizeBytes)
            .ToList();
    }

    private static FastScanFinding MergeFindings(FastScanFinding left, FastScanFinding right)
    {
        string? mergedPrimaryPath = MergePrimaryPath(left.PrimaryPath, right.PrimaryPath);
        long mergedSize = left.TotalSizeBytes + right.TotalSizeBytes;
        bool isExactSize = left.IsExactSize && right.IsExactSize;
        bool isHotspot = left.IsHotspot || right.IsHotspot;
        var mergedBucket = MergeBuckets(left.OriginalBucket, right.OriginalBucket, mergedPrimaryPath, left.AppId, left.Category);

        return left with
        {
            SizeBytes = mergedSize,
            PrimaryPath = mergedPrimaryPath,
            SourcePath = left.TotalSizeBytes >= right.TotalSizeBytes ? left.SourcePath : right.SourcePath,
            IsExactSize = isExactSize,
            DisplaySize = isExactSize
                ? CDriveMaster.Core.Utilities.SizeFormatter.Format(mergedSize)
                : $"> {CDriveMaster.Core.Utilities.SizeFormatter.Format(mergedSize)}",
            IsExperimental = left.IsExperimental || right.IsExperimental,
            IsHotspot = isHotspot,
            IsResidual = left.IsResidual || right.IsResidual,
            IsHeuristicMatch = left.IsHeuristicMatch || right.IsHeuristicMatch,
            Trace = MergeTrace(left.Trace, right.Trace, mergedPrimaryPath, isHotspot),
            OriginalBucket = mergedBucket
        };
    }

    private static ProbeTraceInfo MergeTrace(
        ProbeTraceInfo left,
        ProbeTraceInfo right,
        string? mergedPrimaryPath,
        bool isHotspot)
    {
        return left with
        {
            EvidenceScore = Math.Max(left.EvidenceScore, right.EvidenceScore),
            MatchedEvidences = left.MatchedEvidences
                .Concat(right.MatchedEvidences)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            CandidateDirectories = left.CandidateDirectories
                .Concat(right.CandidateDirectories)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            VerifiedDirectories = left.VerifiedDirectories
                .Concat(right.VerifiedDirectories)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            RejectedDirectories = left.RejectedDirectories
                .Concat(right.RejectedDirectories)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            RejectReasons = left.RejectReasons
                .Concat(right.RejectReasons)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            MatchHistory = left.MatchHistory
                .Concat(right.MatchHistory)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            FinalDecision = isHotspot
                ? $"已聚合多个 {mergedPrimaryPath ?? "应用"} 缓存命中"
                : left.FinalDecision
        };
    }

    private static CleanupBucket? MergeBuckets(
        CleanupBucket? left,
        CleanupBucket? right,
        string? mergedPrimaryPath,
        string appId,
        string category)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        var entries = left.Entries
            .Concat(right.Entries)
            .DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (entries.Count == 0)
        {
            return null;
        }

        var allowedRoots = (left.AllowedRoots ?? Array.Empty<string>())
            .Concat(right.AllowedRoots ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string rootPath = MergePrimaryPath(left.RootPath, right.RootPath)
            ?? mergedPrimaryPath
            ?? left.RootPath;

        return new CleanupBucket(
            BucketId: $"aggregate:{appId}:{Guid.NewGuid():N}",
            Category: category,
            RootPath: rootPath,
            AppName: appId,
            RiskLevel: left.RiskLevel == RiskLevel.SafeAuto && right.RiskLevel == RiskLevel.SafeAuto
                ? RiskLevel.SafeAuto
                : RiskLevel.SafeWithPreview,
            SuggestedAction: left.SuggestedAction,
            Description: $"Aggregated hotspot candidate - {appId}",
            EstimatedSizeBytes: entries.Sum(entry => entry.SizeBytes),
            Entries: entries.AsReadOnly(),
            AllowedRoots: allowedRoots);
    }

    private static string? MergePrimaryPath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return right;
        }

        if (string.IsNullOrWhiteSpace(right))
        {
            return left;
        }

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return left;
        }

        return GetCommonAncestor(left, right) ?? left;
    }

    private static bool IsSafeCleanupFile(string filePath)
    {
        string extension = Path.GetExtension(filePath);
        return string.IsNullOrWhiteSpace(extension) || !UnsafeCleanupExtensions.Contains(extension);
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

    private static IEnumerable<string> EnumerateRuleAnchorRoots(CleanupRule rule)
    {
        var appKeywords = EnumerateAppKeywords(rule)
            .ToArray();

        foreach (var root in EnumerateRuleRoots(rule))
        {
            string? anchor = TryGetAnchorRoot(root, appKeywords);
            if (!string.IsNullOrWhiteSpace(anchor))
            {
                yield return anchor;
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

    private static IEnumerable<string> EnumerateResidualKeywords(CleanupRule rule)
    {
        if (rule.ResidualFingerprints is null)
        {
            if (rule.FastScan?.SearchHints is null)
            {
                yield break;
            }
        }

        foreach (var fingerprint in rule.ResidualFingerprints ?? new List<ResidualFingerprint>())
        {
            foreach (var keyword in fingerprint.PathKeywords)
            {
                if (!string.IsNullOrWhiteSpace(keyword))
                {
                    yield return keyword.Trim();
                }
            }
        }

        if (rule.FastScan?.SearchHints is null)
        {
            yield break;
        }

        foreach (var hint in rule.FastScan.SearchHints)
        {
            foreach (var keyword in hint.DirectoryKeywords)
            {
                if (!string.IsNullOrWhiteSpace(keyword))
                {
                    yield return keyword.Trim();
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

    private static IEnumerable<string> EnumerateRuleSearchParents(CleanupRule rule)
    {
        if (rule.FastScan?.HeuristicSearchHints is not null)
        {
            foreach (var hint in rule.FastScan.HeuristicSearchHints)
            {
                string? expanded = ExpandRulePath(hint.Parent);
                if (!string.IsNullOrWhiteSpace(expanded))
                {
                    yield return expanded;
                }
            }
        }

        if (rule.FastScan?.SearchHints is not null)
        {
            foreach (var hint in rule.FastScan.SearchHints)
            {
                string? expanded = ExpandRulePath(hint.Parent);
                if (!string.IsNullOrWhiteSpace(expanded))
                {
                    yield return expanded;
                }
            }
        }

        if (rule.ResidualFingerprints is null)
        {
            yield break;
        }

        foreach (var fingerprint in rule.ResidualFingerprints)
        {
            string? expanded = ExpandRulePath(fingerprint.Parent);
            if (!string.IsNullOrWhiteSpace(expanded))
            {
                yield return expanded;
            }
        }
    }

    private static IEnumerable<string> DiscoverRuleRootsFromSearchParents(CleanupRule rule)
    {
        var keywords = EnumerateAppKeywords(rule)
            .Concat(EnumerateResidualKeywords(rule))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (keywords.Length == 0)
        {
            yield break;
        }

        foreach (var parentRoot in EnumerateRuleSearchParents(rule))
        {
            if (string.IsNullOrWhiteSpace(parentRoot) || !Directory.Exists(parentRoot))
            {
                continue;
            }

            foreach (var path in DiscoverMatchingDirectories(parentRoot, keywords, maxDepth: 2))
            {
                yield return path;
            }
        }
    }

    private static void AddTargetedRoot(IDictionary<string, int> roots, string? rootPath, int priority)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return;
        }

        string normalized = rootPath.TrimEnd('\\');
        if (roots.TryGetValue(normalized, out int existingPriority) && existingPriority <= priority)
        {
            return;
        }

        roots[normalized] = priority;
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

    private static IEnumerable<string> EnumeratePathSegments(string normalizedPath)
    {
        return normalizedPath
            .Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !string.IsNullOrWhiteSpace(segment));
    }

    private static bool MatchesNormalizedKeyword(
        string normalizedPathToken,
        IReadOnlyList<string> normalizedSegments,
        string keyword)
    {
        string normalizedKeyword = NormalizePathToken(keyword);
        if (string.IsNullOrWhiteSpace(normalizedKeyword))
        {
            return false;
        }

        return normalizedPathToken.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase)
            || normalizedSegments.Any(segment => segment.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePathToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static bool IsSameOrAncestorPath(string candidateAncestor, string path)
    {
        return string.Equals(candidateAncestor, path, StringComparison.OrdinalIgnoreCase)
            || IsAncestorPath(candidateAncestor, path);
    }

    private static bool IsAncestorPath(string candidateAncestor, string path)
    {
        if (string.IsNullOrWhiteSpace(candidateAncestor) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalizedAncestor = candidateAncestor.TrimEnd('\\');
        string normalizedPath = path.TrimEnd('\\');
        if (normalizedPath.Length <= normalizedAncestor.Length)
        {
            return false;
        }

        return normalizedPath.StartsWith(normalizedAncestor + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetScanModeLabel(BasicScanMode scanMode)
    {
        return scanMode switch
        {
            BasicScanMode.Fast => "快速扫描",
            BasicScanMode.FullDisk => "全盘深度扫描",
            BasicScanMode.AppDeep => "应用深度扫描",
            _ => "扫描"
        };
    }

    private enum BasicScanMode
    {
        Fast,
        FullDisk,
        AppDeep
    }

    private static IEnumerable<string> DiscoverMatchingDirectories(
        string rootPath,
        IReadOnlyList<string> keywords,
        int maxDepth)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || keywords.Count == 0 || maxDepth < 0)
        {
            yield break;
        }

        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((rootPath, 0));

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false
        };

        while (pending.Count > 0)
        {
            var (currentPath, depth) = pending.Dequeue();
            if (depth >= maxDepth)
            {
                continue;
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(currentPath, "*", options);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                string directoryName = Path.GetFileName(directory);
                string normalizedName = NormalizePathToken(directoryName);
                if (keywords.Any(keyword => normalizedName.Contains(NormalizePathToken(keyword), StringComparison.OrdinalIgnoreCase)))
                {
                    yield return directory.TrimEnd('\\');
                }

                pending.Enqueue((directory, depth + 1));
            }
        }
    }

    private static string? TryResolveParentKeywordAnchorRoot(string normalizedPath, string parentRoot, CleanupRule rule)
    {
        if (string.IsNullOrWhiteSpace(parentRoot)
            || !normalizedPath.StartsWith(parentRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string relativePath = normalizedPath[parentRoot.Length..].TrimStart('\\');
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var matchKeywords = EnumerateAppKeywords(rule)
            .Concat(EnumerateResidualKeywords(rule))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matchKeywords.Length == 0)
        {
            return null;
        }

        var relativeSegments = relativePath
            .Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < relativeSegments.Length; i++)
        {
            string normalizedSegment = NormalizePathToken(relativeSegments[i]);
            if (string.IsNullOrWhiteSpace(normalizedSegment))
            {
                continue;
            }

            if (!matchKeywords.Any(keyword => normalizedSegment.Contains(NormalizePathToken(keyword), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return CombinePath(parentRoot, relativeSegments.Take(i + 1));
        }

        return null;
    }

    private static string CombinePath(string parentRoot, IEnumerable<string> segments)
    {
        string normalizedRoot = parentRoot.TrimEnd('\\');
        string suffix = string.Join("\\", segments);
        return string.IsNullOrWhiteSpace(suffix)
            ? normalizedRoot
            : $"{normalizedRoot}\\{suffix}";
    }

    private static string? TryGetAnchorRoot(string path, IReadOnlyList<string> appKeywords)
    {
        if (string.IsNullOrWhiteSpace(path) || appKeywords.Count == 0)
        {
            return null;
        }

        var segments = path
            .Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
        for (int i = segments.Length - 1; i >= 0; i--)
        {
            if (!appKeywords.Any(keyword => segments[i].Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (i == 0 && segments[0].EndsWith(":", StringComparison.OrdinalIgnoreCase))
            {
                return segments[0] + "\\";
            }

            return string.Join("\\", segments.Take(i + 1));
        }

        return null;
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

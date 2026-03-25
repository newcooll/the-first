using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CDriveMaster.Core.Interfaces;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;
using CDriveMaster.UI.Services;
using CDriveMaster.UI.ViewModels.Items;

namespace CDriveMaster.UI.ViewModels;

public partial class GenericCleanupViewModel : ObservableObject
{
    private readonly RuleCatalog catalog;
    private readonly ICleanupPipeline pipeline;
    private readonly IDialogService dialogService;
    private readonly IPreviewDialogService previewService;
    private readonly AuditLogExporter auditLogExporter;

    public ObservableCollection<ICleanupProvider> AvailableApps { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private ICleanupProvider? selectedApp;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplySafeAutoCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReviewAndApplyCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplySafeAutoCommand))]
    private bool hasSafeAutoItems;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReviewAndApplyCommand))]
    private bool hasSafeWithPreviewItems;

    [ObservableProperty]
    private string statusText = "准备就绪";

    [ObservableProperty]
    private ObservableCollection<BucketResultItemViewModel> bucketItems = new();

    [ObservableProperty]
    private ExecutionSummaryViewModel? executionSummary;

    public GenericCleanupViewModel(
        RuleCatalog catalog,
        ICleanupPipeline pipeline,
        IDialogService dialogService,
        IPreviewDialogService previewService,
        AuditLogExporter auditLogExporter)
    {
        this.catalog = catalog;
        this.pipeline = pipeline;
        this.dialogService = dialogService;
        this.previewService = previewService;
        this.auditLogExporter = auditLogExporter;

        foreach (var provider in catalog.GetAllProviders())
        {
            AvailableApps.Add(provider);
        }

        SelectedApp = AvailableApps.FirstOrDefault();

        if (this.catalog.FailedRuleErrors.Count > 0)
        {
            _ = this.dialogService.ShowErrorAsync(
                "规则加载警告",
                string.Join("\n", this.catalog.FailedRuleErrors));
        }
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        var selectedProvider = SelectedApp;
        if (selectedProvider is null)
            return;

        IsBusy = true;
        StatusText = $"正在扫描 {selectedProvider.AppName} 数据...";

        try
        {
            var results = await Task.Run(() =>
            {
                var buckets = selectedProvider.GetBuckets();
                if (buckets.Count == 0)
                {
                    return Array.Empty<BucketResult>();
                }

                return pipeline.Execute(buckets, apply: false);
            });

            var sortedItems = results
                .Select(x => new BucketResultItemViewModel(x))
                .OrderBy(x => RiskSortKey(x.RawRisk))
                .ThenByDescending(x => x.RawEstimatedSize)
                .ToList();

            BucketItems.Clear();
            foreach (var item in sortedItems)
            {
                BucketItems.Add(item);
            }

            HasSafeAutoItems = BucketItems.Any(x => x.RawRisk == RiskLevel.SafeAuto);
            HasSafeWithPreviewItems = BucketItems.Any(x => x.RawRisk == RiskLevel.SafeWithPreview);
            ExecutionSummary = null;

            StatusText = results.Count == 0
                ? $"未发现 {selectedProvider.AppName} 可清理数据"
                : $"{selectedProvider.AppName} 扫描完成";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            HasSafeAutoItems = false;
            HasSafeWithPreviewItems = false;
            ExecutionSummary = null;
            StatusText = $"扫描失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplySafeAuto))]
    private async Task ApplySafeAutoAsync()
    {
        var selectedProvider = SelectedApp;
        if (selectedProvider is null)
        {
            return;
        }

        var targets = BucketItems
            .Where(x => x.RawRisk == RiskLevel.SafeAuto && x.OriginalResult.FinalStatus != ExecutionStatus.Success)
            .ToList();

        if (targets.Count == 0)
        {
            await dialogService.ShowInfoAsync("无可执行项", "当前没有可执行的 SafeAuto 项目。请先扫描或检查状态。");
            return;
        }

        long estimatedBytes = targets.Sum(x => x.OriginalResult.Bucket.EstimatedSizeBytes);
        double estimatedMb = estimatedBytes / 1024.0 / 1024.0;
        bool confirmed = await dialogService.ConfirmAsync(
            "确认物理清理",
            $"即将物理清理 {selectedProvider.AppName} 的 {targets.Count} 个 SafeAuto 项目，预计释放 {estimatedMb:F2} MB 空间。此操作不可逆，是否继续？");

        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在物理清理...";

        try
        {
            var targetBuckets = targets
                .Select(x => x.OriginalResult.Bucket)
                .ToList();

            var executeResults = await Task.Run(() => pipeline.Execute(targetBuckets, apply: true));
            var byBucketId = executeResults.ToDictionary(x => x.Bucket.BucketId, x => x);

            for (int i = 0; i < BucketItems.Count; i++)
            {
                var existing = BucketItems[i];
                if (byBucketId.TryGetValue(existing.OriginalResult.Bucket.BucketId, out var updated))
                {
                    BucketItems[i] = new BucketResultItemViewModel(updated);
                }
            }

            int successCount = executeResults.Count(x => x.FinalStatus == ExecutionStatus.Success);
            StatusText = $"物理清理完成，成功 {successCount}/{executeResults.Count} 项";

            ExecutionSummary ??= new ExecutionSummaryViewModel();
            ExecutionSummary.UpdateFrom(executeResults);
            HasSafeAutoItems = BucketItems.Any(x =>
                x.RawRisk == RiskLevel.SafeAuto &&
                x.OriginalResult.FinalStatus != ExecutionStatus.Success);
            HasSafeWithPreviewItems = BucketItems.Any(x =>
                x.RawRisk == RiskLevel.SafeWithPreview &&
                x.OriginalResult.FinalStatus != ExecutionStatus.Success);

            if (executeResults.Any(x => x.FinalStatus != ExecutionStatus.Skipped))
            {
                await auditLogExporter.ExportAsync(selectedProvider.AppName, executeResults);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            await dialogService.ShowErrorAsync("物理清理失败", ex.Message);
            StatusText = $"物理清理失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanReviewAndApply))]
    private async Task ReviewAndApplyAsync()
    {
        var previewTargets = BucketItems
            .Where(x => x.RawRisk == RiskLevel.SafeWithPreview && x.OriginalResult.FinalStatus != ExecutionStatus.Success)
            .Select(x => x.OriginalResult.Bucket)
            .ToList();

        if (previewTargets.Count == 0)
        {
            return;
        }

        var allEntries = previewTargets
            .SelectMany(x => x.Entries)
            .ToList();

        var selectedEntries = (await previewService.ShowPreviewAsync("预览并确认清理项", allEntries)).ToList();
        if (selectedEntries.Count == 0)
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在按预览清单执行清理...";

        try
        {
            var byPath = previewTargets
                .SelectMany(b => b.Entries.Select(e => new { Bucket = b, Entry = e }))
                .GroupBy(x => x.Entry.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Bucket, StringComparer.OrdinalIgnoreCase);

            var grouped = selectedEntries
                .Where(x => byPath.ContainsKey(x.Path))
                .GroupBy(x => byPath[x.Path].BucketId)
                .ToList();

            var executeResults = new System.Collections.Generic.List<BucketResult>();
            foreach (var group in grouped)
            {
                var parentBucket = previewTargets.First(x => x.BucketId == group.Key);
                var result = await Task.Run(() => pipeline.ExecuteEntries(parentBucket, group, apply: true));
                executeResults.Add(result);
            }

            var byBucketId = executeResults.ToDictionary(x => x.Bucket.BucketId, x => x);
            for (int i = 0; i < BucketItems.Count; i++)
            {
                var existing = BucketItems[i];
                if (byBucketId.TryGetValue(existing.OriginalResult.Bucket.BucketId, out var updated))
                {
                    BucketItems[i] = new BucketResultItemViewModel(updated);
                }
            }

            ExecutionSummary ??= new ExecutionSummaryViewModel();
            ExecutionSummary.UpdateFrom(executeResults);

            HasSafeAutoItems = BucketItems.Any(x =>
                x.RawRisk == RiskLevel.SafeAuto &&
                x.OriginalResult.FinalStatus != ExecutionStatus.Success);
            HasSafeWithPreviewItems = BucketItems.Any(x =>
                x.RawRisk == RiskLevel.SafeWithPreview &&
                x.OriginalResult.FinalStatus != ExecutionStatus.Success);

            if (executeResults.Any(x => x.FinalStatus != ExecutionStatus.Skipped))
            {
                var appName = SelectedApp?.AppName ?? "Unknown";
                await auditLogExporter.ExportAsync(appName, executeResults);
            }

            StatusText = $"预览清理完成，共处理 {executeResults.Count} 个分组";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            await dialogService.ShowErrorAsync("预览清理失败", ex.Message);
            StatusText = $"预览清理失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanScan() => SelectedApp is not null && !IsBusy;

    private bool CanApplySafeAuto() => HasSafeAutoItems && !IsBusy;

    private bool CanReviewAndApply() => HasSafeWithPreviewItems && !IsBusy;

    [RelayCommand]
    private void OpenRulesFolder()
    {
        OpenFolder("Rules");
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        OpenFolder("Logs");
    }

    private static int RiskSortKey(RiskLevel risk)
    {
        return risk switch
        {
            RiskLevel.SafeAuto => 0,
            RiskLevel.SafeWithPreview => 1,
            RiskLevel.Blocked => 2,
            _ => 3
        };
    }

    private static void OpenFolder(string folderName)
    {
        string fullPath = Path.Combine(AppContext.BaseDirectory, folderName);
        Directory.CreateDirectory(fullPath);

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = fullPath,
            UseShellExecute = true
        });
    }
}
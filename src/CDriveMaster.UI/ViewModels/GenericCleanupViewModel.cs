using System;
using System.Collections.ObjectModel;
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
    private readonly ICleanupPipeline pipeline;
    private readonly IDialogService dialogService;

    public ObservableCollection<ICleanupProvider> AvailableApps { get; } = new();

    [ObservableProperty]
    private ICleanupProvider? selectedApp;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusText = "准备就绪";

    [ObservableProperty]
    private ObservableCollection<BucketResultItemViewModel> bucketItems = new();

    public GenericCleanupViewModel(RuleCatalog catalog, ICleanupPipeline pipeline, IDialogService dialogService)
    {
        this.pipeline = pipeline;
        this.dialogService = dialogService;

        foreach (var provider in catalog.GetAllProviders())
        {
            AvailableApps.Add(provider);
        }

        SelectedApp = AvailableApps.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (SelectedApp is null)
        {
            return;
        }

        IsBusy = true;
        StatusText = $"正在扫描 {SelectedApp.AppName} 数据...";

        try
        {
            var selectedProvider = SelectedApp;
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

            StatusText = results.Count == 0
                ? $"未发现 {selectedProvider.AppName} 可清理数据"
                : $"{selectedProvider.AppName} 扫描完成";
        }
        catch (Exception ex)
        {
            StatusText = $"扫描失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplySafeAutoAsync()
    {
        if (SelectedApp is null)
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
            $"即将物理清理 {SelectedApp.AppName} 的 {targets.Count} 个 SafeAuto 项目，预计释放 {estimatedMb:F2} MB 空间。此操作不可逆，是否继续？");

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
        }
        catch (Exception ex)
        {
            await dialogService.ShowErrorAsync("物理清理失败", ex.Message);
            StatusText = $"物理清理失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
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
}
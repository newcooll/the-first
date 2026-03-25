using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CDriveMaster.Core.Interfaces;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;
using CDriveMaster.UI.ViewModels.Items;

namespace CDriveMaster.UI.ViewModels;

public partial class GenericCleanupViewModel : ObservableObject
{
    private readonly CleanupPipeline pipeline;

    public ObservableCollection<ICleanupProvider> AvailableApps { get; } = new();

    [ObservableProperty]
    private ICleanupProvider? selectedApp;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusText = "准备就绪";

    [ObservableProperty]
    private ObservableCollection<BucketResultItemViewModel> bucketItems = new();

    public GenericCleanupViewModel(RuleCatalog catalog, CleanupPipeline pipeline)
    {
        this.pipeline = pipeline;

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
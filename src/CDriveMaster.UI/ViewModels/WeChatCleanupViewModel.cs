using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Providers;
using CDriveMaster.Core.Services;

namespace CDriveMaster.UI.ViewModels;

public partial class WeChatCleanupViewModel : ObservableObject
{
    private readonly WeChatCleanupProvider provider;
    private readonly CleanupPipeline pipeline;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusText = "准备就绪";

    [ObservableProperty]
    private ObservableCollection<BucketResult> bucketResults = new();

    public WeChatCleanupViewModel(WeChatCleanupProvider provider, CleanupPipeline pipeline)
    {
        this.provider = provider;
        this.pipeline = pipeline;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsBusy = true;
        StatusText = "正在扫描微信缓存...";

        try
        {
            var results = await Task.Run(() =>
            {
                var buckets = provider.GetBuckets();
                if (buckets.Count == 0)
                {
                    return Array.Empty<BucketResult>();
                }

                return pipeline.Execute(buckets, apply: false);
            });

            BucketResults.Clear();
            foreach (var result in results)
            {
                BucketResults.Add(result);
            }

            StatusText = results.Count == 0 ? "未发现微信数据" : "扫描完成";
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
}

using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;

namespace CDriveMaster.UI.ViewModels;

public partial class SystemMaintenanceViewModel : ObservableObject
{
    private readonly DismAnalyzer analyzer;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusText = "准备就绪";

    [ObservableProperty]
    private string actualSizeText = "--";

    [ObservableProperty]
    private string estimatedReclaimableText = "--";

    [ObservableProperty]
    private bool cleanupRecommended;

    public SystemMaintenanceViewModel(DismAnalyzer analyzer)
    {
        this.analyzer = analyzer;
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        IsBusy = true;
        StatusText = "正在分析系统组件存储 (WinSxS)，请稍候...";

        try
        {
            var result = await analyzer.AnalyzeAsync(Guid.NewGuid().ToString("N"));
            if (result.Status == ExecutionStatus.Success && result.Report is not null)
            {
                ActualSizeText = $"{ToGbString(result.Report.ActualSizeBytes)} GB";
                EstimatedReclaimableText = $"{ToGbString(result.Report.EstimatedReclaimableBytes)} GB";
                CleanupRecommended = result.Report.CleanupRecommended;
                StatusText = "分析完成";
            }
            else
            {
                ActualSizeText = "0.00 GB";
                EstimatedReclaimableText = "0.00 GB";
                CleanupRecommended = false;
                StatusText = result.Reason;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string ToGbString(long bytes)
    {
        double value = bytes / 1024.0 / 1024.0 / 1024.0;
        return value.ToString("F2");
    }
}

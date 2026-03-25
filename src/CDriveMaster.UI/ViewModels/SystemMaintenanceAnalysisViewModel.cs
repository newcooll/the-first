using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CDriveMaster.Core.Executors;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;
using CDriveMaster.UI.Services;

namespace CDriveMaster.UI.ViewModels;

public partial class SystemMaintenanceAnalysisViewModel : ObservableObject
{
    private readonly DismAnalyzer analyzer;
    private readonly DismCleanupExecutor cleanupExecutor;
    private readonly IDialogService dialogService;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanupCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanupCommand))]
    private bool hasAnalysisResult;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanupCommand))]
    private bool isConfirmed;

    [ObservableProperty]
    private string statusText = "准备就绪，点击分析获取系统组件(WinSxS)状态";

    [ObservableProperty]
    private string actualSizeText = "--";

    [ObservableProperty]
    private string estimatedReclaimableText = "--";

    [ObservableProperty]
    private string cleanupRecommended = "--";

    [ObservableProperty]
    private string riskDescription = "系统组件存储(WinSxS)包含 Windows 更新和备份文件。请仅在 C 盘空间严重不足时进行清理。";

    [ObservableProperty]
    private string lastAnalysisTime = "--";

    [ObservableProperty]
    private SystemCleanupViewModel? cleanupResult;

    public SystemMaintenanceAnalysisViewModel(
        DismAnalyzer analyzer,
        DismCleanupExecutor cleanupExecutor,
        IDialogService dialogService)
    {
        this.analyzer = analyzer;
        this.cleanupExecutor = cleanupExecutor;
        this.dialogService = dialogService;
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        IsBusy = true;
        StatusText = "正在分析系统组件存储，这通常需要几分钟时间，请耐心等待...";

        try
        {
            var result = await analyzer.AnalyzeAsync(Guid.NewGuid().ToString("N"));
            if (result.Status == ExecutionStatus.Success && result.Report is not null)
            {
                ActualSizeText = ToGbText(result.Report.ActualSizeBytes);
                EstimatedReclaimableText = ToGbText(result.Report.EstimatedReclaimableBytes);
                CleanupRecommended = result.Report.CleanupRecommended ? "是" : "否";
                LastAnalysisTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                StatusText = "系统组件分析完成";
                CleanupResult = null;
                HasAnalysisResult = result.Report.CleanupRecommended;
                IsConfirmed = false;
            }
            else
            {
                StatusText = "分析失败";
                HasAnalysisResult = false;
                await dialogService.ShowErrorAsync("分析失败", string.IsNullOrWhiteSpace(result.Reason) ? result.StdErr : result.Reason);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCleanup))]
    private async Task CleanupAsync()
    {
        IsBusy = true;
        StatusText = "正在执行系统组件真实清理，请勿强制关闭程序 (约 10-30 分钟)...";

        try
        {
            var result = await cleanupExecutor.ExecuteAsync(Guid.NewGuid().ToString("N"));
            CleanupResult = new SystemCleanupViewModel(
                Title: "WinSxS 组件清理",
                Status: result.Status,
                Duration: result.Duration,
                ExitCode: result.ExitCode,
                Message: result.Status == ExecutionStatus.Success ? "执行完成" : "执行失败或被拦截");

            HasAnalysisResult = false;
            IsConfirmed = false;
            StatusText = "清理完毕，建议重新分析";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanAnalyze() => !IsBusy;

    private bool CanCleanup() => !IsBusy && HasAnalysisResult && IsConfirmed;

    private static string ToGbText(long bytes)
    {
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }
}
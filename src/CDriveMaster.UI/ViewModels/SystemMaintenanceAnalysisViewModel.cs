using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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
    private readonly AuditLogExporter auditLogExporter;
    private readonly DispatcherTimer? driveSpaceTimer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanupCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReAnalyzeCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanupCommand))]
    private bool hasAnalysisResult;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanupCommand))]
    private bool isConfirmed;

    [ObservableProperty]
    private string statusText = "准备就绪，先分析 WinSxS 组件存储，再决定是否执行系统瘦身。";

    [ObservableProperty]
    private string actualSizeText = "--";

    [ObservableProperty]
    private string estimatedReclaimableText = "--";

    [ObservableProperty]
    private string cleanupRecommended = "--";

    [ObservableProperty]
    private string riskDescription = "系统瘦身只调用 Windows 原生 DISM 组件清理，不会触碰个人文件，但会占用较长时间并建议在管理员权限下执行。";

    [ObservableProperty]
    private string lastAnalysisTime = "--";

    [ObservableProperty]
    private string cleanupDecisionText = "需先完成分析后判断是否建议执行。";

    [ObservableProperty]
    private string safetyBoundaryText = "仅执行 DISM /Online /Cleanup-Image /StartComponentCleanup /English，不包含 /ResetBase，保留更新回退能力。";

    [ObservableProperty]
    private string cleanupImpactText = "目标仅限 WinSxS 组件存储的可回收部分；个人文件、桌面文档、下载目录不会被此功能触碰。";

    [ObservableProperty]
    private long cDriveFreeBytes;

    [ObservableProperty]
    private long cDriveTotalBytes;

    [ObservableProperty]
    private string cDriveUsageText = "C 盘空间加载中...";

    [ObservableProperty]
    private SystemCleanupViewModel? cleanupResult;

    public SystemMaintenanceAnalysisViewModel(
        DismAnalyzer analyzer,
        DismCleanupExecutor cleanupExecutor,
        IDialogService dialogService,
        AuditLogExporter auditLogExporter)
    {
        this.analyzer = analyzer;
        this.cleanupExecutor = cleanupExecutor;
        this.dialogService = dialogService;
        this.auditLogExporter = auditLogExporter;

        RefreshCDriveSpaceStatus();
        if (Application.Current is not null)
        {
            driveSpaceTimer = new DispatcherTimer(DispatcherPriority.Background, Application.Current.Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            driveSpaceTimer.Tick += (_, _) => RefreshCDriveSpaceStatus();
            driveSpaceTimer.Start();
        }
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        IsBusy = true;
        RefreshCDriveSpaceStatus();
        StatusText = "正在分析 WinSxS 组件存储，这通常需要几分钟。";

        try
        {
            var result = await analyzer.AnalyzeAsync(Guid.NewGuid().ToString("N"));
            if (result.Status == ExecutionStatus.Success && result.Report is not null)
            {
                ActualSizeText = ToGbText(result.Report.ActualSizeBytes);
                EstimatedReclaimableText = ToGbText(result.Report.EstimatedReclaimableBytes);
                CleanupRecommended = result.Report.CleanupRecommended ? "建议执行" : "当前无需执行";
                CleanupDecisionText = result.Report.CleanupRecommended
                    ? "当前机器检测到可回收组件，系统瘦身功能可用。确认后会调用 DISM 进行组件清理。"
                    : "当前机器暂无明显收益，建议暂不执行系统瘦身。";
                LastAnalysisTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                StatusText = "WinSxS 分析完成";
                CleanupResult = null;
                HasAnalysisResult = result.Report.CleanupRecommended;
                IsConfirmed = false;
            }
            else
            {
                StatusText = "WinSxS 分析失败";
                CleanupDecisionText = "分析失败，暂不建议执行系统瘦身。请先查看审计日志。";
                HasAnalysisResult = false;
                await dialogService.ShowErrorAsync(
                    "分析失败",
                    string.IsNullOrWhiteSpace(result.Reason) ? result.StdErr : result.Reason);
            }
        }
        finally
        {
            RefreshCDriveSpaceStatus();
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCleanup))]
    private async Task CleanupAsync()
    {
        IsBusy = true;
        RefreshCDriveSpaceStatus();
        StatusText = "正在执行系统瘦身，DISM 组件清理通常需要 10 到 30 分钟。";

        try
        {
            var result = await cleanupExecutor.ExecuteAsync(Guid.NewGuid().ToString("N"));
            await auditLogExporter.ExportSystemMaintenanceAsync(result);

            CleanupResult = new SystemCleanupViewModel(
                Title: "WinSxS 组件清理",
                Status: result.Status,
                Duration: result.Duration,
                ExitCode: result.ExitCode,
                Message: result.Status == ExecutionStatus.Success ? "执行完成" : "执行失败或被拦截");

            CleanupDecisionText = result.Status == ExecutionStatus.Success
                ? "系统瘦身已执行完成。建议重新分析一次，确认实际回收效果。"
                : "系统瘦身执行失败或被拦截，请先查看审计日志再决定是否重试。";
            HasAnalysisResult = false;
            IsConfirmed = false;
            StatusText = "系统瘦身执行完毕，建议重新分析。";
        }
        finally
        {
            RefreshCDriveSpaceStatus();
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task ReAnalyzeAsync()
    {
        ResetAnalysisState();
        await AnalyzeAsync();
    }

    [RelayCommand]
    private async Task OpenLogsFolderAsync()
    {
        try
        {
            string logsPath = Path.Combine(AppContext.BaseDirectory, "Logs");
            Directory.CreateDirectory(logsPath);

            Process.Start(new ProcessStartInfo
            {
                FileName = logsPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            await dialogService.ShowErrorAsync("打开日志目录失败", ex.Message);
        }
    }

    internal void RefreshCDriveSpaceStatus()
    {
        var snapshot = ReadDriveSpace();
        CDriveFreeBytes = snapshot.FreeBytes;
        CDriveTotalBytes = snapshot.TotalBytes;
        if (snapshot.TotalBytes <= 0)
        {
            CDriveUsageText = $"{snapshot.DriveLabel} 空间信息不可用";
            return;
        }

        long usedBytes = Math.Max(0, snapshot.TotalBytes - snapshot.FreeBytes);
        double freePercent = snapshot.TotalBytes == 0
            ? 0
            : snapshot.FreeBytes * 100d / snapshot.TotalBytes;
        CDriveUsageText =
            $"{snapshot.DriveLabel} 可用 {CDriveMaster.Core.Utilities.SizeFormatter.Format(snapshot.FreeBytes)} / " +
            $"总计 {CDriveMaster.Core.Utilities.SizeFormatter.Format(snapshot.TotalBytes)} | " +
            $"已用 {CDriveMaster.Core.Utilities.SizeFormatter.Format(usedBytes)} | 可用率 {freePercent:F1}%";
    }

    internal static DriveSpaceSnapshot ReadDriveSpace()
    {
        string driveRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        try
        {
            var drive = new DriveInfo(driveRoot);
            string label = string.IsNullOrWhiteSpace(drive.Name)
                ? "C 盘"
                : drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return new DriveSpaceSnapshot(label, drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch
        {
            return new DriveSpaceSnapshot("C 盘", 0, 0);
        }
    }

    private bool CanAnalyze() => !IsBusy;

    private bool CanCleanup() => !IsBusy && HasAnalysisResult && IsConfirmed;

    private void ResetAnalysisState()
    {
        ActualSizeText = "--";
        EstimatedReclaimableText = "--";
        CleanupRecommended = "--";
        LastAnalysisTime = "--";
        CleanupResult = null;
        HasAnalysisResult = false;
        IsConfirmed = false;
        CleanupDecisionText = "需先完成分析后判断是否建议执行。";
    }

    private static string ToGbText(long bytes)
    {
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }

    internal sealed record DriveSpaceSnapshot(string DriveLabel, long FreeBytes, long TotalBytes);
}

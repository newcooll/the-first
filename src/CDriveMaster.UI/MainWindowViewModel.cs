using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CDriveMaster.Core.Services;
using CDriveMaster.Core.Models;
using Microsoft.VisualBasic.FileIO;

namespace CDriveMaster.UI;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IDiskScanService _scanService;
    private readonly IRecycleBinService _recycleBinService;
    private readonly ITempCleanupService _tempCleanupService = new TempCleanupService();

    [ObservableProperty]
    private string _statusText = "准备就绪";

    [ObservableProperty]
    private int _progressValue = 0;

    [ObservableProperty]
    private ScanSnapshot? _lastSnapshot;

    [ObservableProperty]
    private RecycleBinInfo _recycleBinState = new(0, 0);

    [ObservableProperty]
    private TempCleanupResult? _lastTempCleanupResult;

    [ObservableProperty]
    private FileStat? _selectedFile;

    public MainWindowViewModel(IDiskScanService scanService, IRecycleBinService recycleBinService)
    {
        _scanService = scanService;
        _recycleBinService = recycleBinService;

        _ = LoadRecycleBinAsync();
    }

    [RelayCommand]
    private async Task LoadRecycleBinAsync()
    {
        RecycleBinState = await _recycleBinService.QueryAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task EmptyRecycleBinAsync()
    {
        var result = await _recycleBinService.EmptyAsync(showConfirmation: true, showProgressUi: true, playSound: true, CancellationToken.None);
        if (result.Success)
        {
            double mb = result.ReleasedBytes / 1024.0 / 1024.0;
            StatusText = $"回收站清理完成，成功释放了 {mb:F2} MB 空间。";
            await LoadRecycleBinAsync();
        }
        else
        {
            StatusText = result.ErrorMessage ?? "回收站清理取消或失败。";
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task CleanTempAsync(CancellationToken token)
    {
        try
        {
            StatusText = "正在执行 Temp 目录安全清理...";
            ProgressValue = 0;
            LastTempCleanupResult = null;

            var progress = new Progress<CleanupProgress>(p =>
            {
                StatusText = p.CurrentStep;
                ProgressValue = (ProgressValue + 5) % 100;
            });

            var result = await _tempCleanupService.CleanupAsync(token, progress);
            LastTempCleanupResult = result;

            double mb = result.ReleasedBytes / 1024.0 / 1024.0;
            StatusText = $"Temp 清理结束。成功释放 {mb:F2} MB，具体拦截数据请查看 [Temp 清理报告] 页签。";
            ProgressValue = 100;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Temp 清理已由用户安全中止。";
            ProgressValue = 0;
        }
        catch (Exception ex)
        {
            StatusText = $"Temp 清理发生未捕获异常: {ex.Message}";
            ProgressValue = 0;
        }
    }

    [RelayCommand]
    private void DeleteToRecycleBin(FileStat? file)
    {
        if (file == null || !File.Exists(file.Path))
        {
            MessageBox.Show("文件不存在或已被删除。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        double mb = file.Size / 1024.0 / 1024.0;
        string msg = $"确定要将以下文件移入回收站吗？\n\n文件名: {file.Name}\n大小: {mb:F2} MB\n路径: {file.Path}\n\n注意: 文件将移至回收站，不会被永久抹除。";

        var result = MessageBox.Show(msg, "安全删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            try
            {
                FileSystem.DeleteFile(file.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                StatusText = $"已将 [{file.Name}] 移至回收站。";

                _ = LoadRecycleBinAsync();

                if (LastSnapshot != null)
                {
                    var updatedFiles = LastSnapshot.LargestFiles.Where(f => f.Path != file.Path).ToList().AsReadOnly();
                    LastSnapshot = LastSnapshot with { LargestFiles = updatedFiles };
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"移入回收站失败: {ex.Message}", "删除异常", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private void OpenFolder(FileStat? file)
    {
        if (file == null) return;
        var dir = Path.GetDirectoryName(file.Path);
        if (Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
    }

    [RelayCommand]
    private void CopyPath(FileStat? file)
    {
        if (file != null)
        {
            Clipboard.SetText(file.Path);
            StatusText = "文件路径已复制到剪贴板。";
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ScanAsync(CancellationToken token)
    {
        try
        {
            ProgressValue = 0;
            StatusText = "正在初始化扫描计划...";
            LastSnapshot = null;

            var progress = new Progress<ScanProgress>(p =>
            {
                StatusText = p.CurrentStep;
                if (p.Percent > 0) ProgressValue = p.Percent;
            });

            var targets = new List<ScanTarget>
            {
                new ScanTarget(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), "Downloads"),
                new ScanTarget(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Desktop"),
                new ScanTarget(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents"),
                new ScanTarget(Path.GetTempPath(), "Temp")
            };

            LastSnapshot = await _scanService.ScanAsync(targets, token, progress);

            await LoadRecycleBinAsync();

            double totalGb = LastSnapshot.TotalBytes / 1024.0 / 1024.0 / 1024.0;
            StatusText = $"全盘扫描完成！共 {LastSnapshot.TotalFiles} 个文件，总计 {totalGb:F2} GB，跳过 {LastSnapshot.Issues.Count} 个无权访问项。";
            ProgressValue = 100;
        }
        catch (OperationCanceledException)
        {
            StatusText = "扫描已由用户取消。";
            ProgressValue = 0;
        }
        catch (Exception ex)
        {
            StatusText = $"扫描失败: {ex.Message}";
            ProgressValue = 0;
        }
    }
}

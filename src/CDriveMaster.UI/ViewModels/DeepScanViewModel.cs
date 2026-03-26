using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;
using CDriveMaster.UI.Services;

namespace CDriveMaster.UI.ViewModels;

public partial class DeepScanViewModel : ObservableObject
{
    private readonly DeepScanService deepScanService;
    private readonly INavigationService navigationService;
    private CancellationTokenSource? cts;

    [ObservableProperty]
    private DeepScanResult? scanResult;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    private bool isScanning;

    [ObservableProperty]
    private string statusText = "准备扫描...";

    public DeepScanViewModel(DeepScanService deepScanService, INavigationService navigationService)
    {
        this.deepScanService = deepScanService;
        this.navigationService = navigationService;
    }

    public async Task InitializeAsync(string targetPath, string title)
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = new CancellationTokenSource();

        IsScanning = true;
        StatusText = "正在深度扫描，请稍候...";
        ScanResult = null;

        try
        {
            var result = await deepScanService.ScanAsync(targetPath, title, cts.Token);
            ScanResult = result;
            StatusText = $"扫描完成，发现 {result.FileCount} 个文件";
        }
        catch (OperationCanceledException)
        {
            StatusText = "扫描已取消";
        }
        finally
        {
            IsScanning = false;
            cts?.Dispose();
            cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    private void CancelScan()
    {
        cts?.Cancel();
    }

    private bool CanCancelScan() => IsScanning;
}

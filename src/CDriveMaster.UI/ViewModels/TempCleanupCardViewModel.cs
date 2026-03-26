using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;
using CDriveMaster.Core.Utilities;
using CDriveMaster.UI.Services;

namespace CDriveMaster.UI.ViewModels;

public partial class TempCleanupCardViewModel : ObservableObject
{
    private readonly RuleCatalog ruleCatalog;
    private readonly ICleanupPipeline cleanupPipeline;
    private readonly AuditLogExporter auditLogExporter;
    private readonly IDialogService dialogService;
    private long totalEstimatedBytes;

    public ObservableCollection<CleanupBucket> TempBuckets { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCleanupCommand))]
    private bool isScanning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCleanupCommand))]
    private string displaySize = "计算中...";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCleanupCommand))]
    private string statusText = "准备扫描";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCleanupCommand))]
    private bool canExecuteCleanup;

    public TempCleanupCardViewModel(
        RuleCatalog ruleCatalog,
        ICleanupPipeline cleanupPipeline,
        AuditLogExporter auditLogExporter,
        IDialogService dialogService)
    {
        this.ruleCatalog = ruleCatalog;
        this.cleanupPipeline = cleanupPipeline;
        this.auditLogExporter = auditLogExporter;
        this.dialogService = dialogService;
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        IsScanning = true;
        CanExecuteCleanup = false;
        StatusText = "正在扫描系统临时文件...";

        try
        {
            var provider = ruleCatalog.GetAllProviders()
                .FirstOrDefault(p => string.Equals(p.AppName, "系统临时文件", StringComparison.OrdinalIgnoreCase));

            if (provider is null)
            {
                TempBuckets.Clear();
                totalEstimatedBytes = 0;
                DisplaySize = "0.00 B";
                StatusText = "未找到系统临时文件规则";
                return;
            }

            var buckets = provider.GetBuckets();
            var results = await Task.Run(() => cleanupPipeline.Execute(buckets, apply: false));
            totalEstimatedBytes = results.Sum(r => r.Bucket.EstimatedSizeBytes);

            ReplaceTempBuckets(buckets);

            DisplaySize = SizeFormatter.Format(totalEstimatedBytes);
            StatusText = "扫描完成";
            CanExecuteCleanup = totalEstimatedBytes > 0;
        }
        catch (Exception ex)
        {
            TempBuckets.Clear();
            totalEstimatedBytes = 0;
            DisplaySize = "0.00 B";
            StatusText = "扫描失败";
            await dialogService.ShowErrorAsync("系统临时文件扫描失败", ex.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteCleanupAction))]
    private async Task ExecuteCleanupAsync()
    {
        IsScanning = true;
        CanExecuteCleanup = false;
        StatusText = "正在清理...";

        try
        {
            var provider = ruleCatalog.GetAllProviders()
                .FirstOrDefault(p => string.Equals(p.AppName, "系统临时文件", StringComparison.OrdinalIgnoreCase));

            if (provider is null)
            {
                StatusText = "未找到系统临时文件规则";
                return;
            }

            var buckets = provider.GetBuckets();
            var results = await Task.Run(() => cleanupPipeline.Execute(buckets, apply: true));
            await auditLogExporter.ExportAsync("系统临时文件", results);

            long reclaimedBytes = results
                .Where(r => r.FinalStatus == ExecutionStatus.Success || r.FinalStatus == ExecutionStatus.PartialSuccess)
                .Sum(r => r.ReclaimedSizeBytes);

            await dialogService.ShowInfoAsync("清理完成", $"成功释放了 {SizeFormatter.Format(reclaimedBytes)}");

            StatusText = "清理完成，正在刷新统计...";
        }
        catch (Exception ex)
        {
            StatusText = "清理失败";
            await dialogService.ShowErrorAsync("系统临时文件清理失败", ex.Message);
        }
        finally
        {
            IsScanning = false;
        }

        await ScanAsync();
    }

    private bool CanScan() => !IsScanning;

    private bool CanExecuteCleanupAction() => !IsScanning && totalEstimatedBytes > 0;

    private void ReplaceTempBuckets(IReadOnlyList<CleanupBucket> buckets)
    {
        TempBuckets.Clear();
        foreach (var bucket in buckets)
        {
            TempBuckets.Add(bucket);
        }
    }
}

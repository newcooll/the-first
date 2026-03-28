using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CDriveMaster.Core.Models;

namespace CDriveMaster.UI.ViewModels.Items;

public partial class ExecutionSummaryViewModel : ObservableObject
{
    [ObservableProperty]
    private int successCount;

    [ObservableProperty]
    private int failedCount;

    [ObservableProperty]
    private int skippedCount;

    [ObservableProperty]
    private string reclaimedSizeText = "0 MB";

    [ObservableProperty]
    private string summaryMessage = string.Empty;

    public void UpdateFrom(IEnumerable<BucketResult> results)
    {
        var list = results.ToList();

        SuccessCount = list.Count(x => x.FinalStatus == ExecutionStatus.Success);
        FailedCount = list.Count(x => x.FinalStatus == ExecutionStatus.Failed);
        SkippedCount = list.Count - SuccessCount - FailedCount;

        long reclaimedBytes = list.Sum(x => x.ReclaimedSizeBytes);
        ReclaimedSizeText = FormatBytes(reclaimedBytes);
        SummaryMessage = $"执行完成：成功 {SuccessCount} 项，失败 {FailedCount} 项，其他 {SkippedCount} 项";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
        {
            return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
        }

        return $"{bytes / 1024.0 / 1024.0:F2} MB";
    }
}

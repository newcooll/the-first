using CommunityToolkit.Mvvm.ComponentModel;
using CDriveMaster.Core.Models;

namespace CDriveMaster.UI.ViewModels.Items;

public partial class BucketResultItemViewModel : ObservableObject
{
    private readonly BucketResult source;

    public BucketResultItemViewModel(BucketResult source)
    {
        this.source = source;
    }

    public BucketResult OriginalResult => source;

    public string AppName => source.Bucket.AppName;

    public string CategoryDescription => source.Bucket.Description;

    public string RiskLevelText => source.Bucket.RiskLevel switch
    {
        RiskLevel.SafeAuto => "安全（可自动）",
        RiskLevel.SafeWithPreview => "安全（需预览）",
        RiskLevel.Blocked => "高危拦截",
        _ => source.Bucket.RiskLevel.ToString()
    };

    public string FormattedEstimatedSize => FormatBytes(source.Bucket.EstimatedSizeBytes);

    public string FormattedReclaimedSize => FormatBytes(source.ReclaimedSizeBytes);

    public string StatusText => source.FinalStatus switch
    {
        ExecutionStatus.Success => "成功",
        ExecutionStatus.PartialSuccess => "部分成功",
        ExecutionStatus.Blocked => "已拦截",
        ExecutionStatus.Skipped => "未执行",
        ExecutionStatus.Failed => "失败",
        _ => source.FinalStatus.ToString()
    };

    public RiskLevel RawRisk => source.Bucket.RiskLevel;

    public long RawEstimatedSize => source.Bucket.EstimatedSizeBytes;

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
        {
            return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
        }

        return $"{bytes / 1024.0 / 1024.0:F2} MB";
    }
}

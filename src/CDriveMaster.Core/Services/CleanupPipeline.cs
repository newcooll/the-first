using System.Collections.Generic;
using System.Linq;
using CDriveMaster.Core.Executors;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Services;

public class CleanupPipeline
{
    private readonly DryRunExecutor dryRunExecutor;
    private readonly CleanupExecutor cleanupExecutor;

    public CleanupPipeline(DryRunExecutor dryRunExecutor, CleanupExecutor cleanupExecutor)
    {
        this.dryRunExecutor = dryRunExecutor;
        this.cleanupExecutor = cleanupExecutor;
    }

    public IReadOnlyList<BucketResult> Execute(IReadOnlyList<CleanupBucket> buckets, bool apply)
    {
        var logs = apply
            ? cleanupExecutor.Execute(buckets)
            : dryRunExecutor.Execute(buckets);

        var logsByBucket = logs
            .GroupBy(x => x.BucketId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AuditLogItem>)g.ToList());

        var results = new List<BucketResult>(buckets.Count);

        foreach (var bucket in buckets)
        {
            logsByBucket.TryGetValue(bucket.BucketId, out var bucketLogs);
            bucketLogs ??= new List<AuditLogItem>();

            var finalStatus = AuditAggregator.CalculateBucketStatus(bucketLogs);
            long reclaimedBytes = bucketLogs
                .Where(x => x.Status == ExecutionStatus.Success)
                .Sum(x => x.TargetSizeBytes);
            int successCount = bucketLogs.Count(x => x.Status == ExecutionStatus.Success);
            int failedCount = bucketLogs.Count(x => x.Status == ExecutionStatus.Failed);

            results.Add(new BucketResult(
                Bucket: bucket,
                FinalStatus: finalStatus,
                ReclaimedSizeBytes: reclaimedBytes,
                SuccessCount: successCount,
                FailedCount: failedCount,
                Logs: bucketLogs));
        }

        return results.AsReadOnly();
    }
}

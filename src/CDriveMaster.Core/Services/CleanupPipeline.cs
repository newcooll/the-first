using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CDriveMaster.Core.Executors;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Services;

public class CleanupPipeline : ICleanupPipeline
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
            int blockedCount = bucketLogs.Count(x => x.Status == ExecutionStatus.Blocked);

            results.Add(new BucketResult(
                Bucket: bucket,
                FinalStatus: finalStatus,
                ReclaimedSizeBytes: reclaimedBytes,
                SuccessCount: successCount,
                FailedCount: failedCount,
                BlockedCount: blockedCount,
                Logs: bucketLogs));
        }

        return results.AsReadOnly();
    }

    public Task<IReadOnlyList<BucketResult>> ExecuteAsync(
        IReadOnlyList<CleanupBucket> buckets,
        bool apply,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Execute(buckets, apply), cancellationToken);
    }

    public BucketResult ExecuteEntries(
        CleanupBucket parentBucket,
        IEnumerable<CleanupEntry> entriesToApply,
        bool apply,
        bool allowTrustedExactFileFastPath = false)
    {
        var entries = entriesToApply.ToList();
        var tempBucket = new CleanupBucket(
            BucketId: parentBucket.BucketId,
            Category: parentBucket.Category,
            RootPath: parentBucket.RootPath,
            AppName: parentBucket.AppName,
            RiskLevel: parentBucket.RiskLevel,
            SuggestedAction: parentBucket.SuggestedAction,
            Description: parentBucket.Description,
            EstimatedSizeBytes: entries.Sum(x => x.SizeBytes),
            Entries: entries.AsReadOnly(),
            AllowedRoots: parentBucket.AllowedRoots);

        var result = apply
            ? BuildResults(tempBucket, cleanupExecutor.Execute(new[] { tempBucket }, allowTrustedExactFileFastPath))
            : BuildResults(tempBucket, dryRunExecutor.Execute(new[] { tempBucket }));
        return result[0];
    }

    private static IReadOnlyList<BucketResult> BuildResults(CleanupBucket bucket, IReadOnlyList<AuditLogItem> logs)
    {
        var finalStatus = AuditAggregator.CalculateBucketStatus(logs);
        long reclaimedBytes = logs
            .Where(x => x.Status == ExecutionStatus.Success)
            .Sum(x => x.TargetSizeBytes);
        int successCount = logs.Count(x => x.Status == ExecutionStatus.Success);
        int failedCount = logs.Count(x => x.Status == ExecutionStatus.Failed);
        int blockedCount = logs.Count(x => x.Status == ExecutionStatus.Blocked);

        return new[]
        {
            new BucketResult(
                Bucket: bucket,
                FinalStatus: finalStatus,
                ReclaimedSizeBytes: reclaimedBytes,
                SuccessCount: successCount,
                FailedCount: failedCount,
                BlockedCount: blockedCount,
                Logs: logs)
        };
    }
}

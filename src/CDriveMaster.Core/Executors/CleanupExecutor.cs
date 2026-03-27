using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CDriveMaster.Core.Guards;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Executors;

public class CleanupExecutor
{
    private readonly PreflightGuard guard;
    private readonly string jobId;
    private readonly ICleanupDeleteBackend deleteBackend;

    public CleanupExecutor(PreflightGuard guard, string jobId, ICleanupDeleteBackend? deleteBackend = null)
    {
        this.guard = guard;
        this.jobId = jobId;
        this.deleteBackend = deleteBackend ?? new WindowsCleanupDeleteBackend();
    }

    public IReadOnlyList<AuditLogItem> Execute(IReadOnlyList<CleanupBucket> buckets)
    {
        var logs = new List<AuditLogItem>();

        foreach (var bucket in buckets)
        {
            var executableEntries = new List<CleanupEntry>();
            foreach (var entry in bucket.Entries)
            {
                var preflight = guard.CheckPath(entry.Path, bucket.AllowedRoots ?? new[] { bucket.RootPath });
                if (!preflight.Passed)
                {
                    logs.Add(new AuditLogItem(
                        JobId: jobId,
                        BucketId: bucket.BucketId,
                        TimestampUtc: DateTime.UtcNow,
                        TargetPath: entry.Path,
                        TargetSizeBytes: entry.SizeBytes,
                        Action: bucket.SuggestedAction,
                        Risk: bucket.RiskLevel,
                        AppName: bucket.AppName,
                        Reason: preflight.Reason,
                        Status: ExecutionStatus.Blocked,
                        ErrorMessage: null));
                    continue;
                }
                executableEntries.Add(entry);
            }

            if (executableEntries.Count == 0)
            {
                continue;
            }

            var sizeByPath = executableEntries
                .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().SizeBytes, StringComparer.OrdinalIgnoreCase);

            foreach (var result in deleteBackend.DeleteMany(executableEntries, bucket.SuggestedAction))
            {
                if (result.Status == ExecutionStatus.Skipped && !string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    Debug.WriteLine($"Cleanup skipped: {result.Path}. {result.ErrorMessage}");
                }

                logs.Add(new AuditLogItem(
                    JobId: jobId,
                    BucketId: bucket.BucketId,
                    TimestampUtc: DateTime.UtcNow,
                    TargetPath: result.Path,
                    TargetSizeBytes: sizeByPath.TryGetValue(result.Path, out var sizeBytes) ? sizeBytes : 0,
                    Action: bucket.SuggestedAction,
                    Risk: bucket.RiskLevel,
                    AppName: bucket.AppName,
                    Reason: "Passed",
                    Status: result.Status,
                    ErrorMessage: result.ErrorMessage));
            }
        }

        return logs.AsReadOnly();
    }
}

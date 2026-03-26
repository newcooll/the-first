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

    public CleanupExecutor(PreflightGuard guard, string jobId)
    {
        this.guard = guard;
        this.jobId = jobId;
    }

    public IReadOnlyList<AuditLogItem> Execute(IReadOnlyList<CleanupBucket> buckets)
    {
        var logs = new List<AuditLogItem>();

        foreach (var bucket in buckets)
        {
            foreach (var entry in bucket.Entries)
            {
                var preflight = guard.CheckPath(entry.Path);
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

                try
                {
                    if (entry.IsDirectory)
                    {
                        if (Directory.Exists(entry.Path))
                        {
                            Directory.Delete(entry.Path, recursive: true);
                        }
                    }
                    else
                    {
                        if (File.Exists(entry.Path))
                        {
                            File.Delete(entry.Path);
                        }
                    }

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
                        Status: ExecutionStatus.Success,
                        ErrorMessage: null));
                }
                catch (IOException ex)
                {
                    Debug.WriteLine($"Cleanup skipped due to IO lock: {entry.Path}. {ex.Message}");
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
                        Status: ExecutionStatus.Skipped,
                        ErrorMessage: ex.Message));
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    Debug.WriteLine($"Cleanup skipped due to permission: {entry.Path}. {ex.Message}");
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
                        Status: ExecutionStatus.Skipped,
                        ErrorMessage: ex.Message));
                    continue;
                }
                catch (Exception ex)
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
                        Status: ExecutionStatus.Failed,
                        ErrorMessage: ex.Message));
                    continue;
                }
            }
        }

        return logs.AsReadOnly();
    }
}

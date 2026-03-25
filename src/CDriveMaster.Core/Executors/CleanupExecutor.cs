using System;
using System.Collections.Generic;
using CDriveMaster.Core.Guards;
using CDriveMaster.Core.Models;
using Microsoft.VisualBasic.FileIO;

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
                        FileSystem.DeleteDirectory(
                            entry.Path,
                            UIOption.OnlyErrorDialogs,
                            RecycleOption.SendToRecycleBin,
                            UICancelOption.DoNothing);
                    }
                    else
                    {
                        FileSystem.DeleteFile(
                            entry.Path,
                            UIOption.OnlyErrorDialogs,
                            RecycleOption.SendToRecycleBin,
                            UICancelOption.DoNothing);
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

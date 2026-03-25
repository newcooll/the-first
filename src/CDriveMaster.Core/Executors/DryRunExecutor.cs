using System;
using System.Collections.Generic;
using CDriveMaster.Core.Guards;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Executors;

public class DryRunExecutor
{
    private readonly PreflightGuard preflightGuard;
    private readonly string jobId;

    public DryRunExecutor(PreflightGuard? preflightGuard = null, string? jobId = null)
    {
        this.preflightGuard = preflightGuard ?? new PreflightGuard();
        this.jobId = jobId ?? Guid.NewGuid().ToString("N");
    }

    public IReadOnlyList<AuditLogItem> Execute(IReadOnlyList<CleanupBucket> buckets)
    {
        var logs = new List<AuditLogItem>();

        foreach (var bucket in buckets)
        {
            foreach (var entry in bucket.Entries)
            {
                var check = preflightGuard.CheckPath(entry.Path);
                if (!check.Passed)
                {
                    logs.Add(new AuditLogItem(
                        JobId: this.jobId,
                        TimestampUtc: DateTime.UtcNow,
                        TargetPath: entry.Path,
                        Action: bucket.SuggestedAction,
                        Risk: bucket.RiskLevel,
                        AppName: bucket.AppName,
                        Reason: check.Reason,
                        Status: ExecutionStatus.Blocked,
                        ErrorMessage: null));
                    continue;
                }

                logs.Add(new AuditLogItem(
                    JobId: this.jobId,
                    TimestampUtc: DateTime.UtcNow,
                    TargetPath: entry.Path,
                    Action: bucket.SuggestedAction,
                    Risk: bucket.RiskLevel,
                    AppName: bucket.AppName,
                    Reason: "DryRun: Passed Preflight, physical deletion skipped.",
                    Status: ExecutionStatus.Skipped,
                    ErrorMessage: null));
            }
        }

        return logs.AsReadOnly();
    }
}

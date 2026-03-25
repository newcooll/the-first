using System;
using System.Collections.Generic;
using System.IO;
using CDriveMaster.Core.Models;
using Microsoft.VisualBasic.FileIO;

namespace CDriveMaster.Core.Services;

public class CleanupExecutor
{
    public IReadOnlyList<AuditLogItem> ExecuteBucket(CleanupBucket bucket)
    {
        var auditLogs = new List<AuditLogItem>(bucket.Entries.Count);
        var jobId = Guid.NewGuid().ToString("N");

        foreach (var entry in bucket.Entries)
        {
            if (string.Equals(entry.Path, bucket.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                auditLogs.Add(new AuditLogItem(
                    JobId: jobId,
                    TimestampUtc: DateTime.UtcNow,
                    TargetPath: entry.Path,
                    Action: bucket.SuggestedAction,
                    Risk: bucket.RiskLevel,
                    AppName: bucket.AppName,
                    Reason: bucket.Description,
                    Status: ExecutionStatus.Blocked,
                    ErrorMessage: "RootPath is protected and cannot be deleted."));
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

                auditLogs.Add(new AuditLogItem(
                    JobId: jobId,
                    TimestampUtc: DateTime.UtcNow,
                    TargetPath: entry.Path,
                    Action: bucket.SuggestedAction,
                    Risk: bucket.RiskLevel,
                    AppName: bucket.AppName,
                    Reason: bucket.Description,
                    Status: ExecutionStatus.Success,
                    ErrorMessage: null));
            }
            catch (IOException ex)
            {
                auditLogs.Add(new AuditLogItem(
                    JobId: jobId,
                    TimestampUtc: DateTime.UtcNow,
                    TargetPath: entry.Path,
                    Action: bucket.SuggestedAction,
                    Risk: bucket.RiskLevel,
                    AppName: bucket.AppName,
                    Reason: bucket.Description,
                    Status: ExecutionStatus.Failed,
                    ErrorMessage: ex.Message));
            }
            catch (UnauthorizedAccessException ex)
            {
                auditLogs.Add(new AuditLogItem(
                    JobId: jobId,
                    TimestampUtc: DateTime.UtcNow,
                    TargetPath: entry.Path,
                    Action: bucket.SuggestedAction,
                    Risk: bucket.RiskLevel,
                    AppName: bucket.AppName,
                    Reason: bucket.Description,
                    Status: ExecutionStatus.Failed,
                    ErrorMessage: ex.Message));
            }
            catch (Exception ex)
            {
                auditLogs.Add(new AuditLogItem(
                    JobId: jobId,
                    TimestampUtc: DateTime.UtcNow,
                    TargetPath: entry.Path,
                    Action: bucket.SuggestedAction,
                    Risk: bucket.RiskLevel,
                    AppName: bucket.AppName,
                    Reason: bucket.Description,
                    Status: ExecutionStatus.Failed,
                    ErrorMessage: ex.Message));
            }
        }

        return auditLogs.AsReadOnly();
    }
}

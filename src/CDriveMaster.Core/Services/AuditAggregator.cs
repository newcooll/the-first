using System.Collections.Generic;
using System.Linq;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Services;

public static class AuditAggregator
{
    public static ExecutionStatus CalculateBucketStatus(IEnumerable<AuditLogItem> logs)
    {
        if (logs is null)
        {
            return ExecutionStatus.Skipped;
        }

        var items = logs as IReadOnlyCollection<AuditLogItem> ?? logs.ToArray();
        if (items.Count == 0)
        {
            return ExecutionStatus.Skipped;
        }

        bool hasSuccess = items.Any(x => x.Status == ExecutionStatus.Success);
        bool hasFailed = items.Any(x => x.Status == ExecutionStatus.Failed);
        bool hasBlocked = items.Any(x => x.Status == ExecutionStatus.Blocked);
        bool hasSkipped = items.Any(x => x.Status == ExecutionStatus.Skipped);

        if (hasSkipped && !hasSuccess && !hasFailed && !hasBlocked)
        {
            return ExecutionStatus.Skipped;
        }

        if (hasBlocked && !hasSuccess && !hasFailed && !hasSkipped)
        {
            return ExecutionStatus.Blocked;
        }

        if (hasFailed && !hasSuccess && !hasBlocked && !hasSkipped)
        {
            return ExecutionStatus.Failed;
        }

        if (hasSuccess && (hasFailed || hasBlocked))
        {
            return ExecutionStatus.PartialSuccess;
        }

        if (hasSuccess && !hasFailed && !hasBlocked)
        {
            return ExecutionStatus.Success;
        }

        if (!hasSuccess && hasBlocked && hasSkipped && !hasFailed)
        {
            return ExecutionStatus.Blocked;
        }

        if (!hasSuccess && hasFailed)
        {
            return ExecutionStatus.Failed;
        }

        return ExecutionStatus.Skipped;
    }
}

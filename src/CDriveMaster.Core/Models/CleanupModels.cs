using System.Collections.Generic;

namespace CDriveMaster.Core.Models;

public enum ExecutionStatus
{
    Success,
    PartialSuccess,
    Blocked,
    Skipped,
    Failed
}

public enum RiskLevel
{
    SafeAuto,
    SafeWithPreview,
    Blocked
}

public enum CleanupAction
{
    DeleteToRecycleBin,
    DeletePhysical,
    MigrateDirectory
}

public sealed record AuditLogItem(
    string JobId,
    string BucketId,
    DateTime TimestampUtc,
    string TargetPath,
    long TargetSizeBytes,
    CleanupAction Action,
    RiskLevel Risk,
    string AppName,
    string Reason,
    ExecutionStatus Status,
    string? ErrorMessage
);

public sealed record CleanupEntry(
    string Path,
    bool IsDirectory,
    long SizeBytes,
    DateTime LastWriteTimeUtc,
    string Category
);

public sealed record CleanupBucket(
    string BucketId,
    string Category,
    string RootPath,
    string AppName,
    RiskLevel RiskLevel,
    CleanupAction SuggestedAction,
    string Description,
    long EstimatedSizeBytes,
    IReadOnlyList<CleanupEntry> Entries,
    IReadOnlyList<string>? AllowedRoots = null
);

public sealed record BucketResult(
    CleanupBucket Bucket,
    ExecutionStatus FinalStatus,
    long ReclaimedSizeBytes,
    int SuccessCount,
    int FailedCount,
    int BlockedCount,
    IReadOnlyList<AuditLogItem> Logs
);

public sealed record RecycleBinInfo(long SizeBytes, long ItemCount);

public sealed record CleanupProgress(string CurrentStep);

public sealed record CleanupIssue(string Path, string Reason);

public sealed record TempCleanupResult(
    long ReleasedBytes,
    int DeletedFileCount,
    int DeletedDirectoryCount,
    int SkippedLockedFileCount,
    int SkippedAccessDeniedCount,
    int SkippedReparsePointCount,
    int SkippedTooNewCount,
    int FailedDirectoryDeleteCount,
    IReadOnlyList<CleanupIssue> SampleIssues
);

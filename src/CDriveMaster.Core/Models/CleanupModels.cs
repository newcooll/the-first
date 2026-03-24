using System.Collections.Generic;

namespace CDriveMaster.Core.Models;

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

using System.Collections.Generic;

namespace CDriveMaster.Core.Models;

public enum DeepScanSeverity
{
    Safe,
    Caution,
    Danger
}

public sealed record DeepScanItem(
    string FullPath,
    long SizeBytes,
    string Category,
    DeepScanSeverity Severity,
    string Reason,
    bool IsSelectable
);

public sealed record DeepScanResult(
    string Title,
    string RootPath,
    long ExactSizeBytes,
    int FileCount,
    int DirectoryCount,
    int InaccessibleCount,
    bool HasPermissionIssue,
    IReadOnlyList<DeepScanItem> Items
);

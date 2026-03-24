namespace CDriveMaster.Core.Models;

public sealed record CleanupResult(
    bool Success,
    long ReleasedBytes,
    int AffectedItemCount,
    string? ErrorMessage
);

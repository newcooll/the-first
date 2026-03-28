using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Executors;

public sealed record CleanupDeleteResult(
    string Path,
    ExecutionStatus Status,
    string? ErrorMessage = null,
    string? DetailMessage = null);

public interface ICleanupDeleteBackend
{
    void Delete(CleanupEntry entry, CleanupAction action);

    IReadOnlyList<CleanupDeleteResult> DeleteMany(IReadOnlyList<CleanupEntry> entries, CleanupAction action);
}

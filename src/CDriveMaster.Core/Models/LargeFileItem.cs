using CDriveMaster.Core.Utilities;

namespace CDriveMaster.Core.Models;

public sealed record LargeFileItem(
    string FileName,
    string FilePath,
    long SizeBytes,
    DateTime LastWriteTime
)
{
    public string DisplaySize => SizeFormatter.Format(SizeBytes);
}

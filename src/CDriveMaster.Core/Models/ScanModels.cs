using System;
using System.Collections.Generic;

namespace CDriveMaster.Core.Models;

public sealed record ScanTarget(string Path, string Category);

public sealed record CategoryStat(string Category, long TotalBytes, int TotalFiles, int TotalDirectories);

public sealed record DirectoryStat(string Path, long TotalBytes, int FileCount, int SubdirectoryCount);
public sealed record FileStat(string Path, string Name, long Size, DateTime LastWriteTime);
public sealed record ScanIssue(string Path, string Reason);

public sealed record ScanSnapshot(
    IReadOnlyList<ScanTarget> Targets,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    long TotalBytes,
    int TotalFiles,
    int TotalDirectories,
    IReadOnlyList<CategoryStat> CategoryStats,
    IReadOnlyList<DirectoryStat> LargestDirectories,
    IReadOnlyList<FileStat> LargestFiles,
    IReadOnlyList<ScanIssue> Issues
);

using System.Collections.Generic;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Interfaces;

public sealed record DetectionResult(bool Found, string? BasePath, string Source, string Reason);

public interface IAppDetector
{
    string AppName { get; }

    DetectionResult Detect();
}

public interface ICleanupProvider
{
    string AppName { get; }

    IReadOnlyList<CleanupBucket> GetBuckets();
}

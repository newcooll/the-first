using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Services;

public sealed record ScanProgress(string CurrentStep, int Percent);

public interface IDiskScanService
{
    Task<ScanSnapshot> ScanAsync(
        IReadOnlyList<ScanTarget> targets,
        CancellationToken cancellationToken,
        IProgress<ScanProgress>? progress = null);
}

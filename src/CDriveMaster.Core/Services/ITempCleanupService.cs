using System;
using System.Threading;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Services;

public interface ITempCleanupService
{
    Task<TempCleanupResult> CleanupAsync(CancellationToken cancellationToken, IProgress<CleanupProgress>? progress = null);
}

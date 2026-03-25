using System.Threading;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Services;

public interface IDismCommandRunner
{
    Task<SystemMaintenanceResult> RunAsync(string arguments, string operationId, CancellationToken cancellationToken = default);
}

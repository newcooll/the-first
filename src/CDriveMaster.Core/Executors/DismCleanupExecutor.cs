using System.Threading;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;

namespace CDriveMaster.Core.Executors;

public class DismCleanupExecutor
{
    private readonly IDismCommandRunner runner;

    public DismCleanupExecutor(IDismCommandRunner runner)
    {
        this.runner = runner;
    }

    public Task<SystemMaintenanceResult> ExecuteAsync(string operationId, CancellationToken cancellationToken = default)
    {
        const string arguments = "/Online /Cleanup-Image /StartComponentCleanup /English";
        return runner.RunAsync(arguments, operationId, cancellationToken);
    }
}

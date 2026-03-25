using System.Threading;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Parsers;

namespace CDriveMaster.Core.Services;

public class DismAnalyzer
{
    private readonly DismCommandRunner runner;

    public DismAnalyzer(DismCommandRunner runner)
    {
        this.runner = runner;
    }

    public async Task<SystemMaintenanceReport> AnalyzeAsync(string operationId, CancellationToken cancellationToken = default)
    {
        const string arguments = "/Online /Cleanup-Image /AnalyzeComponentStore /English";
        var result = await runner.RunAsync(arguments, operationId, cancellationToken);

        if (result.Status != ExecutionStatus.Success)
        {
            return new SystemMaintenanceReport(
                OperationId: operationId,
                Name: "Windows Component Store (WinSxS)",
                Risk: RiskLevel.Blocked,
                ActualSizeBytes: 0,
                BackupsAndDisabledFeaturesBytes: 0,
                CacheAndTemporaryDataBytes: 0,
                EstimatedReclaimableBytes: 0,
                ReclaimablePackageCount: 0,
                CleanupRecommended: false,
                RequiresAdmin: true,
                RawOutput: result.StdErr);
        }

        return AnalyzeComponentStoreParser.Parse(result.StdOut, operationId);
    }
}

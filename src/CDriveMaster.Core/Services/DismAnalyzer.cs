using System.Threading;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Parsers;

namespace CDriveMaster.Core.Services;

public class DismAnalyzer
{
    private readonly IDismCommandRunner runner;

    public DismAnalyzer(IDismCommandRunner runner)
    {
        this.runner = runner;
    }

    public async Task<SystemAnalysisResult> AnalyzeAsync(string operationId, CancellationToken cancellationToken = default)
    {
        const string arguments = "/Online /Cleanup-Image /AnalyzeComponentStore /English";
        var result = await runner.RunAsync(arguments, operationId, cancellationToken);

        if (result.Status != ExecutionStatus.Success)
        {
            return new SystemAnalysisResult(
                Status: result.Status,
                Report: null,
                StdOut: result.StdOut,
                StdErr: result.StdErr,
                ExitCode: result.ExitCode,
                Reason: "Command execution failed or blocked.");
        }

        try
        {
            if (string.IsNullOrWhiteSpace(result.StdOut))
            {
                throw new System.FormatException("DISM output is empty.");
            }

            var report = AnalyzeComponentStoreParser.Parse(result.StdOut, operationId);
            return new SystemAnalysisResult(
                Status: ExecutionStatus.Success,
                Report: report,
                StdOut: result.StdOut,
                StdErr: result.StdErr,
                ExitCode: result.ExitCode,
                Reason: "OK");
        }
        catch (System.Exception ex)
        {
            return new SystemAnalysisResult(
                Status: ExecutionStatus.Failed,
                Report: null,
                StdOut: result.StdOut,
                StdErr: result.StdErr,
                ExitCode: result.ExitCode,
                Reason: $"Parsing failed: {ex.Message}");
        }
    }
}

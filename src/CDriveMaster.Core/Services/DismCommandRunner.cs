using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Services;

public class DismCommandRunner : IDismCommandRunner
{
    public async Task<SystemMaintenanceResult> RunAsync(string arguments, string operationId, CancellationToken cancellationToken = default)
    {
        if (!PlatformProbe.IsWindows || !PlatformProbe.IsElevated)
        {
            return new SystemMaintenanceResult(
                OperationId: operationId,
                Status: ExecutionStatus.Blocked,
                EstimatedReclaimableBytes: 0,
                ExitCode: -1,
                Duration: TimeSpan.Zero,
                StdOut: string.Empty,
                StdErr: "DISM requires Windows platform and administrator privileges.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dism.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            process.Start();

            Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stdErrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(cancellationToken);

            string stdOut = await stdOutTask;
            string stdErr = await stdErrTask;

            stopwatch.Stop();
            return new SystemMaintenanceResult(
                OperationId: operationId,
                Status: ExecutionStatus.Success,
                EstimatedReclaimableBytes: 0,
                ExitCode: process.ExitCode,
                Duration: stopwatch.Elapsed,
                StdOut: stdOut,
                StdErr: stdErr);
        }
        catch (Exception ex)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            stopwatch.Stop();
            return new SystemMaintenanceResult(
                OperationId: operationId,
                Status: ExecutionStatus.Failed,
                EstimatedReclaimableBytes: 0,
                ExitCode: -1,
                Duration: stopwatch.Elapsed,
                StdOut: string.Empty,
                StdErr: ex.Message);
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;
using FluentAssertions;
using Xunit;

namespace CDriveMaster.Tests;

public sealed class DismAnalyzerTests
{
    private sealed class StubDismCommandRunner : IDismCommandRunner
    {
        private readonly Func<CancellationToken, Task<SystemMaintenanceResult>> behavior;

        public StubDismCommandRunner(Func<CancellationToken, Task<SystemMaintenanceResult>> behavior)
        {
            this.behavior = behavior;
        }

        public Task<SystemMaintenanceResult> RunAsync(string arguments, string operationId, CancellationToken cancellationToken = default)
        {
            return behavior(cancellationToken);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_WhenRunnerSucceedsAndParses_ShouldReturnSuccessWithReport()
    {
        var raw = """
Actual Size of Component Store : 8.50 GB
Backups and Disabled Features : 1.25 GB
Cache and Temporary Data : 500.00 MB
Number of Reclaimable Packages : 2
Component Store Cleanup Recommended : Yes
""";

        var runner = new StubDismCommandRunner(_ => Task.FromResult(new SystemMaintenanceResult(
            OperationId: "op-1",
            Status: ExecutionStatus.Success,
            EstimatedReclaimableBytes: 0,
            ExitCode: 0,
            Duration: TimeSpan.FromSeconds(1),
            StdOut: raw,
            StdErr: string.Empty)));

        var analyzer = new DismAnalyzer(runner);
        var result = await analyzer.AnalyzeAsync("op-1");

        result.Status.Should().Be(ExecutionStatus.Success);
        result.Report.Should().NotBeNull();
        result.Report!.EstimatedReclaimableBytes.Should().Be(1866465280);
        result.Report.CleanupRecommended.Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeAsync_WhenRunnerBlocked_ShouldReturnBlockedWithoutReport()
    {
        var runner = new StubDismCommandRunner(_ => Task.FromResult(new SystemMaintenanceResult(
            OperationId: "op-2",
            Status: ExecutionStatus.Blocked,
            EstimatedReclaimableBytes: 0,
            ExitCode: -1,
            Duration: TimeSpan.Zero,
            StdOut: string.Empty,
            StdErr: "Admin required.")));

        var analyzer = new DismAnalyzer(runner);
        var result = await analyzer.AnalyzeAsync("op-2");

        result.Status.Should().Be(ExecutionStatus.Blocked);
        result.Report.Should().BeNull();
        result.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task AnalyzeAsync_WhenParserFails_ShouldReturnFailedWithOriginalOutput()
    {
        var runner = new StubDismCommandRunner(_ => Task.FromResult(new SystemMaintenanceResult(
            OperationId: "op-3",
            Status: ExecutionStatus.Success,
            EstimatedReclaimableBytes: 0,
            ExitCode: 0,
            Duration: TimeSpan.Zero,
            StdOut: string.Empty,
            StdErr: "")));

        var analyzer = new DismAnalyzer(runner);
        var result = await analyzer.AnalyzeAsync("op-3");

        result.Status.Should().Be(ExecutionStatus.Failed);
        result.Report.Should().BeNull();
        result.Reason.Should().Contain("Parsing failed");
        result.StdOut.Should().Be(string.Empty);
    }

    [Fact]
    public async Task AnalyzeAsync_WhenCancelled_ShouldThrowTaskCanceledException()
    {
        var runner = new StubDismCommandRunner(ct => Task.FromCanceled<SystemMaintenanceResult>(ct));

        var analyzer = new DismAnalyzer(runner);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = async () => await analyzer.AnalyzeAsync("op-4", cts.Token);
        await act.Should().ThrowAsync<TaskCanceledException>();
    }
}

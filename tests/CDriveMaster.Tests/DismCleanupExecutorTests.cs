using System;
using System.Threading;
using System.Threading.Tasks;
using CDriveMaster.Core.Executors;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;
using FluentAssertions;
using Xunit;

namespace CDriveMaster.Tests;

public sealed class DismCleanupExecutorTests
{
    private sealed class StubDismCommandRunner : IDismCommandRunner
    {
        private readonly SystemMaintenanceResult result;

        public StubDismCommandRunner(SystemMaintenanceResult result)
        {
            this.result = result;
        }

        public string? CapturedArguments { get; private set; }

        public Task<SystemMaintenanceResult> RunAsync(string arguments, string operationId, CancellationToken cancellationToken = default)
        {
            CapturedArguments = arguments;
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPassCorrectArgumentsToRunner()
    {
        var stub = new StubDismCommandRunner(CreateResult());
        var executor = new DismCleanupExecutor(stub);

        _ = await executor.ExecuteAsync("op-1");

        stub.CapturedArguments.Should().Be("/Online /Cleanup-Image /StartComponentCleanup /English");
    }

    [Fact]
    public async Task ExecuteAsync_MustNeverContainResetBaseFlag()
    {
        var stub = new StubDismCommandRunner(CreateResult());
        var executor = new DismCleanupExecutor(stub);

        _ = await executor.ExecuteAsync("op-2");

        stub.CapturedArguments.Should().NotBeNull();
        stub.CapturedArguments!.ToUpperInvariant().Should().NotContain("/RESETBASE");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnRunnerResultDirectly()
    {
        var expected = CreateResult(status: ExecutionStatus.Success, exitCode: 0);
        var stub = new StubDismCommandRunner(expected);
        var executor = new DismCleanupExecutor(stub);

        var actual = await executor.ExecuteAsync("op-3");

        actual.Should().Be(expected);
    }

    private static SystemMaintenanceResult CreateResult(ExecutionStatus status = ExecutionStatus.Success, int exitCode = 0)
    {
        return new SystemMaintenanceResult(
            OperationId: "op",
            Status: status,
            EstimatedReclaimableBytes: 0,
            ExitCode: exitCode,
            Duration: TimeSpan.FromSeconds(1),
            StdOut: "out",
            StdErr: string.Empty);
    }
}

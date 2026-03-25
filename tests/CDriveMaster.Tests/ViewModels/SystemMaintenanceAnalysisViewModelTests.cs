using System;
using System.Threading;
using System.Threading.Tasks;
using CDriveMaster.Core.Executors;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;
using CDriveMaster.Tests.Fakes;
using CDriveMaster.UI.ViewModels;
using FluentAssertions;
using Xunit;

namespace CDriveMaster.Tests.ViewModels;

public sealed class SystemMaintenanceAnalysisViewModelTests
{
    [Fact]
    public void CleanupCommand_CanExecute_ShouldRequireAllGuards()
    {
        var vm = CreateViewModel(CreateAnalyzeRunner(successRecommended: true), CreateCleanupRunner());

        vm.IsBusy = false;
        vm.HasAnalysisResult = false;
        vm.IsConfirmed = false;
        vm.CleanupCommand.CanExecute(null).Should().BeFalse();

        vm.HasAnalysisResult = true;
        vm.IsConfirmed = false;
        vm.CleanupCommand.CanExecute(null).Should().BeFalse();

        vm.HasAnalysisResult = true;
        vm.IsConfirmed = true;
        vm.IsBusy = false;
        vm.CleanupCommand.CanExecute(null).Should().BeTrue();

        vm.IsBusy = true;
        vm.CleanupCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task AnalyzeAsync_WhenRecommended_ShouldUnlockCleanupGuard()
    {
        var vm = CreateViewModel(CreateAnalyzeRunner(successRecommended: true), CreateCleanupRunner());

        await vm.AnalyzeCommand.ExecuteAsync(null);

        vm.HasAnalysisResult.Should().BeTrue();
    }

    [Fact]
    public async Task CleanupAsync_AfterExecution_ShouldLockGuardAndSetResult()
    {
        var expectedDuration = TimeSpan.FromMinutes(12);
        var cleanupRunner = new StubDismCommandRunner(_ => Task.FromResult(new SystemMaintenanceResult(
            OperationId: "cleanup-op",
            Status: ExecutionStatus.Success,
            EstimatedReclaimableBytes: 0,
            ExitCode: 0,
            Duration: expectedDuration,
            StdOut: "ok",
            StdErr: string.Empty)));

        var vm = CreateViewModel(CreateAnalyzeRunner(successRecommended: true), cleanupRunner);
        vm.HasAnalysisResult = true;
        vm.IsConfirmed = true;

        await vm.CleanupCommand.ExecuteAsync(null);

        vm.HasAnalysisResult.Should().BeFalse();
        vm.IsConfirmed.Should().BeFalse();
        vm.CleanupResult.Should().NotBeNull();
        vm.CleanupResult!.Status.Should().Be(ExecutionStatus.Success);
        vm.CleanupResult.Duration.Should().Be(expectedDuration);
    }

    private static SystemMaintenanceAnalysisViewModel CreateViewModel(
        StubDismCommandRunner analyzeRunner,
        StubDismCommandRunner cleanupRunner)
    {
        var analyzer = new DismAnalyzer(analyzeRunner);
        var executor = new DismCleanupExecutor(cleanupRunner);
        var dialog = new FakeDialogService();
        return new SystemMaintenanceAnalysisViewModel(analyzer, executor, dialog);
    }

    private static StubDismCommandRunner CreateAnalyzeRunner(bool successRecommended)
    {
        var raw = $"""
Actual Size of Component Store : 8.50 GB
Backups and Disabled Features : 1.25 GB
Cache and Temporary Data : 500.00 MB
Number of Reclaimable Packages : 2
Component Store Cleanup Recommended : {(successRecommended ? "Yes" : "No")}
""";

        return new StubDismCommandRunner(_ => Task.FromResult(new SystemMaintenanceResult(
            OperationId: "analyze-op",
            Status: ExecutionStatus.Success,
            EstimatedReclaimableBytes: 0,
            ExitCode: 0,
            Duration: TimeSpan.FromSeconds(2),
            StdOut: raw,
            StdErr: string.Empty)));
    }

    private static StubDismCommandRunner CreateCleanupRunner()
    {
        return new StubDismCommandRunner(_ => Task.FromResult(new SystemMaintenanceResult(
            OperationId: "cleanup-op",
            Status: ExecutionStatus.Success,
            EstimatedReclaimableBytes: 0,
            ExitCode: 0,
            Duration: TimeSpan.FromMinutes(5),
            StdOut: "ok",
            StdErr: string.Empty)));
    }

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
}

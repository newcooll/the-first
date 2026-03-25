using System;
using System.IO;
using System.Linq;
using CDriveMaster.Core.Executors;
using CDriveMaster.Core.Guards;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;
using CDriveMaster.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace CDriveMaster.Tests;

public sealed class CleanupExecutorTests
{
    [Fact]
    public void Execute_WhenFileIsLocked_ShouldReportFailedAndContinueOthers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new TempSandbox("cleanup-executor");
        var cacheDir = sandbox.CreateDirectory("test_cache");

        var lockedFilePath = sandbox.CreateFile(Path.Combine("test_cache", "locked_file.tmp"), "locked");
        var normalFilePath = sandbox.CreateFile(Path.Combine("test_cache", "normal_file.tmp"), "normal");

        var builder = new BucketBuilder();
        var bucket = builder.BuildBucket(
            cacheDir,
            "WeChat",
            RiskLevel.SafeAuto,
            CleanupAction.DeleteToRecycleBin,
            "test bucket",
            "Cache");

        bucket.Should().NotBeNull();

        using var lockStream = File.Open(lockedFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var guard = new PreflightGuard();
        var executor = new CDriveMaster.Core.Executors.CleanupExecutor(guard, "test-job");
        var logs = executor.Execute(new[] { bucket! });

        logs.Should().HaveCount(2);

        var lockedLog = logs.Single(x => x.TargetPath.Equals(lockedFilePath, StringComparison.OrdinalIgnoreCase));
        lockedLog.Status.Should().Be(ExecutionStatus.Failed);
        lockedLog.ErrorMessage.Should().NotBeNullOrWhiteSpace();

        var normalLog = logs.Single(x => x.TargetPath.Equals(normalFilePath, StringComparison.OrdinalIgnoreCase));
        normalLog.Status.Should().Be(ExecutionStatus.Success);

        File.Exists(lockedFilePath).Should().BeTrue();
        File.Exists(normalFilePath).Should().BeFalse();
    }
}

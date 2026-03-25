using System;
using System.IO;
using CDriveMaster.Core.Executors;
using CDriveMaster.Core.Guards;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;
using CDriveMaster.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace CDriveMaster.Tests;

public sealed class PhysicalSmokeTests
{
    [Fact]
    public void Apply_SafeAutoBucket_ShouldPhysicallyDeleteAndReportSuccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new TempSandbox("physical-smoke");
        var targetDir = sandbox.CreateDirectory("smoke_target");

        var f1 = sandbox.CreateFile(Path.Combine("smoke_target", "1.tmp"), new string('x', 2048));
        var f2 = sandbox.CreateFile(Path.Combine("smoke_target", "2.tmp"), new string('y', 2048));
        var f3 = sandbox.CreateFile(Path.Combine("smoke_target", "3.tmp"), new string('z', 2048));

        var builder = new BucketBuilder();
        var bucket = builder.BuildBucket(
            targetDir,
            "WeChat",
            RiskLevel.SafeAuto,
            CleanupAction.DeleteToRecycleBin,
            "smoke",
            "Cache");

        bucket.Should().NotBeNull();

        var guard = new PreflightGuard();
        var jobId = "physical-smoke-job";
        var dryRunExecutor = new DryRunExecutor(guard, jobId);
        var cleanupExecutor = new CleanupExecutor(guard, jobId);
        var pipeline = new CleanupPipeline(dryRunExecutor, cleanupExecutor);

        var results = pipeline.Execute(new[] { bucket! }, apply: true);

        results.Should().HaveCount(1);
        results[0].FinalStatus.Should().Be(ExecutionStatus.Success);
        results[0].SuccessCount.Should().Be(3);
        results[0].ReclaimedSizeBytes.Should().Be(6144);

        File.Exists(f1).Should().BeFalse();
        File.Exists(f2).Should().BeFalse();
        File.Exists(f3).Should().BeFalse();
    }
}

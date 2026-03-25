using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CDriveMaster.Core.Executors;
using CDriveMaster.Core.Guards;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;
using CDriveMaster.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace CDriveMaster.Tests;

public sealed class CleanupPipelineTests
{
    [Fact]
    public void Execute_ShouldCorrectlyAggregateBucketResults()
    {
        using var sandbox = new TempSandbox("pipeline-integration");

        var safeCache = sandbox.CreateDirectory("safe_cache");
        var blockedSys = sandbox.CreateDirectory("blocked_sys");

        _ = sandbox.CreateFile(Path.Combine("safe_cache", "a.tmp"), new string('a', 1024));
        _ = sandbox.CreateFile(Path.Combine("safe_cache", "b.tmp"), new string('b', 1024));
        _ = sandbox.CreateFile(Path.Combine("blocked_sys", "c.tmp"), new string('c', 256));

        var builder = new BucketBuilder();
        var bucket1 = builder.BuildBucket(
            safeCache,
            "WeChat",
            RiskLevel.SafeAuto,
            CleanupAction.DeleteToRecycleBin,
            "safe cache",
            "Cache");

        var bucket2 = builder.BuildBucket(
            blockedSys,
            "WeChat",
            RiskLevel.Blocked,
            CleanupAction.DeleteToRecycleBin,
            "blocked bucket",
            "Blocked");

        bucket1.Should().NotBeNull();
        bucket2.Should().NotBeNull();

        var guard = new PreflightGuard();

        var field = typeof(PreflightGuard).GetField("protectedPrefixes", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();

        var prefixes = field!.GetValue(guard) as HashSet<string>;
        prefixes.Should().NotBeNull();
        prefixes!.Add(Path.GetFullPath(blockedSys));

        var jobId = "pipeline-test-job";
        var dryRunExecutor = new DryRunExecutor(guard, jobId);
        var cleanupExecutor = new CleanupExecutor(guard, jobId);
        var pipeline = new CleanupPipeline(dryRunExecutor, cleanupExecutor);

        var results = pipeline.Execute(new[] { bucket1!, bucket2! }, apply: false);

        results.Should().HaveCount(2);

        var safeResult = results.Single(x => x.Bucket.BucketId == bucket1!.BucketId);
        safeResult.FinalStatus.Should().Be(ExecutionStatus.Skipped);
        safeResult.SuccessCount.Should().Be(0);

        var blockedResult = results.Single(x => x.Bucket.BucketId == bucket2!.BucketId);
        blockedResult.FinalStatus.Should().Be(ExecutionStatus.Blocked);
        blockedResult.BlockedCount.Should().Be(1);
    }
}

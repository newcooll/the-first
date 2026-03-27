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
    public void Execute_ShouldPassDeleteToRecycleBinActionToDeleteBackend()
    {
        using var sandbox = new TempSandbox("cleanup-executor-action");
        string filePath = sandbox.CreateFile(Path.Combine("cache", "a.tmp"), "content");
        var backend = new FakeDeleteBackend();
        var bucket = new CleanupBucket(
            BucketId: "bucket-action",
            Category: "Cache",
            RootPath: Path.GetDirectoryName(filePath)!,
            AppName: "Quark",
            RiskLevel: RiskLevel.SafeWithPreview,
            SuggestedAction: CleanupAction.DeleteToRecycleBin,
            Description: "action test",
            EstimatedSizeBytes: 7,
            Entries: new[]
            {
                new CleanupEntry(filePath, false, 7, DateTime.UtcNow, "Cache")
            },
            AllowedRoots: new[] { Path.GetDirectoryName(filePath)! });

        var executor = new CleanupExecutor(new PreflightGuard(), "job-action", backend);

        var logs = executor.Execute(new[] { bucket });

        logs.Should().ContainSingle();
        logs[0].Status.Should().Be(ExecutionStatus.Success);
        backend.BatchCalls.Should().ContainSingle();
        backend.BatchCalls[0].action.Should().Be(CleanupAction.DeleteToRecycleBin);
        backend.BatchCalls[0].entries.Should().ContainSingle(entry => entry.Path == filePath);
    }

    [Fact]
    public void Execute_WhenEntryEscapesAllowedRoots_ShouldBlockWithoutCallingDeleteBackend()
    {
        using var sandbox = new TempSandbox("cleanup-executor-boundary");
        string allowedRoot = sandbox.CreateDirectory("cache");
        string escapedFile = sandbox.CreateFile(Path.Combine("outside", "b.tmp"), "outside");
        var backend = new FakeDeleteBackend();
        var bucket = new CleanupBucket(
            BucketId: "bucket-boundary",
            Category: "Cache",
            RootPath: allowedRoot,
            AppName: "Quark",
            RiskLevel: RiskLevel.SafeWithPreview,
            SuggestedAction: CleanupAction.DeleteToRecycleBin,
            Description: "boundary test",
            EstimatedSizeBytes: 7,
            Entries: new[]
            {
                new CleanupEntry(escapedFile, false, 7, DateTime.UtcNow, "Cache")
            },
            AllowedRoots: new[] { allowedRoot });

        var executor = new CleanupExecutor(new PreflightGuard(), "job-boundary", backend);

        var logs = executor.Execute(new[] { bucket });

        logs.Should().ContainSingle();
        logs[0].Status.Should().Be(ExecutionStatus.Blocked);
        logs[0].Reason.Should().Contain("outside the rule-approved cleanup boundary");
        backend.BatchCalls.Should().BeEmpty();
    }

    [Fact]
    public void Execute_ShouldBatchMultipleEntriesIntoSingleDeleteBackendCall()
    {
        using var sandbox = new TempSandbox("cleanup-executor-batch");
        string firstFile = sandbox.CreateFile(Path.Combine("cache", "a.tmp"), "a");
        string secondFile = sandbox.CreateFile(Path.Combine("cache", "b.tmp"), "b");
        var backend = new FakeDeleteBackend();
        var bucket = new CleanupBucket(
            BucketId: "bucket-batch",
            Category: "Cache",
            RootPath: Path.GetDirectoryName(firstFile)!,
            AppName: "Youku",
            RiskLevel: RiskLevel.SafeWithPreview,
            SuggestedAction: CleanupAction.DeleteToRecycleBin,
            Description: "batch test",
            EstimatedSizeBytes: 2,
            Entries: new[]
            {
                new CleanupEntry(firstFile, false, 1, DateTime.UtcNow, "Cache"),
                new CleanupEntry(secondFile, false, 1, DateTime.UtcNow, "Cache")
            },
            AllowedRoots: new[] { Path.GetDirectoryName(firstFile)! });

        var executor = new CleanupExecutor(new PreflightGuard(), "job-batch", backend);

        var logs = executor.Execute(new[] { bucket });

        logs.Should().HaveCount(2);
        logs.Should().OnlyContain(log => log.Status == ExecutionStatus.Success);
        backend.BatchCalls.Should().ContainSingle();
        backend.BatchCalls[0].entries.Should().HaveCount(2);
    }

    [Fact]
    public void Execute_WhenFileIsLocked_ShouldReportSkippedAndContinueOthers()
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
        lockedLog.Status.Should().Be(ExecutionStatus.Skipped);
        lockedLog.ErrorMessage.Should().NotBeNullOrWhiteSpace();

        var normalLog = logs.Single(x => x.TargetPath.Equals(normalFilePath, StringComparison.OrdinalIgnoreCase));
        normalLog.Status.Should().Be(ExecutionStatus.Success);

        File.Exists(lockedFilePath).Should().BeTrue();
        File.Exists(normalFilePath).Should().BeFalse();
    }

    private sealed class FakeDeleteBackend : ICleanupDeleteBackend
    {
        public List<(IReadOnlyList<CleanupEntry> entries, CleanupAction action)> BatchCalls { get; } = new();

        public void Delete(CleanupEntry entry, CleanupAction action)
        {
            BatchCalls.Add((new[] { entry }, action));
        }

        public IReadOnlyList<CleanupDeleteResult> DeleteMany(IReadOnlyList<CleanupEntry> entries, CleanupAction action)
        {
            BatchCalls.Add((entries, action));
            return entries
                .Select(entry => new CleanupDeleteResult(entry.Path, ExecutionStatus.Success))
                .ToArray();
        }
    }
}

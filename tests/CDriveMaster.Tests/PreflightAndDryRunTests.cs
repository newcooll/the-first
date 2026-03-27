using System;
using System.IO;
using System.Linq;
using CDriveMaster.Core.Executors;
using CDriveMaster.Core.Guards;
using CDriveMaster.Core.Models;
using CDriveMaster.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace CDriveMaster.Tests;

public sealed class PreflightAndDryRunTests
{
    [Fact]
    public void DryRun_should_mark_protected_path_as_blocked_and_valid_sandbox_path_as_skipped()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new TempSandbox("dryrun");
        var okFile = sandbox.CreateFile(Path.Combine("WeChat Files", "wxid_123", "FileStorage", "Cache", "ok.tmp"), "ok");

        var bucket = new CleanupBucket(
            BucketId: "bucket-1",
            Category: "Cache",
            RootPath: Path.GetDirectoryName(okFile)!,
            AppName: "WeChat",
            RiskLevel: RiskLevel.SafeAuto,
            SuggestedAction: CleanupAction.DeleteToRecycleBin,
            Description: "Test bucket",
            EstimatedSizeBytes: 2,
            Entries: new[]
            {
                new CleanupEntry(
                    Path: okFile,
                    IsDirectory: false,
                    SizeBytes: 2,
                    LastWriteTimeUtc: DateTime.UtcNow,
                    Category: "Cache"),

                new CleanupEntry(
                    Path: @"C:\Windows\Temp\Cache\fake.bin",
                    IsDirectory: false,
                    SizeBytes: 1,
                    LastWriteTimeUtc: DateTime.UtcNow,
                    Category: "Cache")
            });

        var executor = new DryRunExecutor(jobId: "test-job");
        var logs = executor.Execute(new[] { bucket });

        logs.Should().HaveCount(2);

        var okLog = logs.Single(x => x.TargetPath == okFile);
        okLog.Status.Should().Be(ExecutionStatus.Skipped);

        var blockedLog = logs.Single(x => x.TargetPath.Equals(@"C:\Windows\Temp\Cache\fake.bin", StringComparison.OrdinalIgnoreCase));
        blockedLog.Status.Should().Be(ExecutionStatus.Blocked);
        blockedLog.Reason.Should().Contain("protected system directory");
    }

    [Fact]
    public void Preflight_preview_fast_path_should_pass_exact_file_boundary_without_fs_probe()
    {
        using var sandbox = new TempSandbox("preflight-preview-fast");
        string missingFile = Path.Combine(sandbox.Combine("AppData", "Youku", "Cache"), "chunk-001.bin");

        var guard = new PreflightGuard();
        var result = guard.CheckPathForPreview(
            missingFile,
            isDirectory: false,
            allowedRoots: new[] { missingFile });

        result.Passed.Should().BeTrue();
        result.Reason.Should().Contain("Preview fast-pass");
    }
}

using System.IO;
using System.Threading;
using CDriveMaster.Core.Interfaces;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Providers;
using CDriveMaster.Core.Services;
using CDriveMaster.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace CDriveMaster.Tests;

public sealed class GenericRuleProviderTests
{
    [Fact]
    public async Task ProbeAsync_WhenFirstHotPathMissing_ShouldFallbackToRealPrimaryPath()
    {
        using var sandbox = new TempSandbox("generic-provider-primary-path");
        string realPath = sandbox.CreateDirectory("RealHotPath");
        _ = sandbox.CreateFile(Path.Combine("RealHotPath", "big.bin"), new string('x', 2048));

        Environment.SetEnvironmentVariable("CDM_REAL_HOT_PATH", realPath);
        try
        {
            var rule = new CleanupRule
            {
                AppName = "ProbeApp",
                Description = "Probe rule",
                DefaultAction = CleanupAction.DeleteToRecycleBin,
                Targets =
                {
                    new TargetRule { BaseFolder = "%CDM_REAL_HOT_PATH%", Kind = "Cache", RiskLevel = RiskLevel.SafeAuto }
                },
                FastScan = new FastScanHint
                {
                    HotPaths = new System.Collections.Generic.List<string>
                    {
                        @"Z:\\DefinitelyMissing\\Nope",
                        "%CDM_REAL_HOT_PATH%"
                    },
                    MaxDepth = 2,
                    MinSizeThreshold = 1,
                    Category = "TestCache"
                }
            };

            var provider = new GenericRuleProvider(
                rule,
                new StubAppDetector(new DetectionResult(true, string.Empty, "FallbackDetector", "test")),
                new BucketBuilder());

            var finding = await provider.ProbeAsync(rule, CancellationToken.None);

            finding.Should().NotBeNull();
            finding!.PrimaryPath.Should().Be(realPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDM_REAL_HOT_PATH", null);
        }
    }

    [Fact]
    public void GetBuckets_WhenDetectorReturnsNotFound_ShouldReturnEmptyList()
    {
        var detector = new StubAppDetector(new DetectionResult(false, null, "TestStub", "Not found"));
        var rule = new CleanupRule
        {
            AppName = "TestApp",
            Description = "Test rule",
            DefaultAction = CleanupAction.DeleteToRecycleBin,
            Targets =
            {
                new TargetRule { BaseFolder = "Cache", Kind = "Cache", RiskLevel = RiskLevel.SafeAuto }
            }
        };

        var provider = new GenericRuleProvider(rule, detector, new BucketBuilder());

        var buckets = provider.GetBuckets();

        buckets.Should().BeEmpty();
    }

    [Fact]
    public void GetBuckets_WithValidTarget_ShouldBuildBucket()
    {
        using var sandbox = new TempSandbox("generic-provider-valid");
        var appRoot = sandbox.CreateDirectory("TestApp");
        _ = sandbox.CreateDirectory("TestApp", "Cache");
        _ = sandbox.CreateFile(Path.Combine("TestApp", "Cache", "a.tmp"), "cache");

        var detector = new StubAppDetector(appRoot);
        var rule = new CleanupRule
        {
            AppName = "TestApp",
            Description = "Test rule",
            DefaultAction = CleanupAction.DeleteToRecycleBin,
            Targets =
            {
                new TargetRule { BaseFolder = "Cache", Kind = "Cache", RiskLevel = RiskLevel.SafeWithPreview }
            }
        };

        var provider = new GenericRuleProvider(rule, detector, new BucketBuilder());

        var buckets = provider.GetBuckets();

        buckets.Should().HaveCount(1);
        buckets[0].RiskLevel.Should().Be(RiskLevel.SafeWithPreview);
        buckets[0].SuggestedAction.Should().Be(CleanupAction.DeleteToRecycleBin);
        buckets[0].RootPath.Should().EndWith(Path.Combine("TestApp", "Cache"));
    }

    [Fact]
    public void GetBuckets_WhenTargetDirectoryThrowsException_ShouldIsolateAndContinue()
    {
        using var sandbox = new TempSandbox("generic-provider-isolation");
        var appRoot = sandbox.CreateDirectory("TestApp");
        _ = sandbox.CreateDirectory("TestApp", "Cache2");
        _ = sandbox.CreateFile(Path.Combine("TestApp", "Cache2", "ok.tmp"), "ok");

        var detector = new StubAppDetector(appRoot);
        var rule = new CleanupRule
        {
            AppName = "TestApp",
            Description = "Test rule",
            DefaultAction = CleanupAction.DeleteToRecycleBin,
            Targets =
            {
                new TargetRule { BaseFolder = "Cache1", Kind = "Cache1", RiskLevel = RiskLevel.SafeAuto },
                new TargetRule { BaseFolder = "Cache2", Kind = "Cache2", RiskLevel = RiskLevel.SafeWithPreview }
            }
        };

        var provider = new GenericRuleProvider(rule, detector, new BucketBuilder());

        var buckets = provider.GetBuckets();

        buckets.Should().HaveCount(1);
        buckets[0].Category.Should().Be("Cache2");
        buckets[0].RootPath.Should().EndWith(Path.Combine("TestApp", "Cache2"));
    }

    [Fact]
    public void GetBuckets_WithEnvironmentVariableAbsolutePathAndNoBasePath_ShouldBuildBucket()
    {
        using var sandbox = new TempSandbox("generic-provider-env");
        var envRoot = sandbox.CreateDirectory("EnvTemp");
        _ = sandbox.CreateFile(Path.Combine("EnvTemp", "sample.tmp"), "env");

        Environment.SetEnvironmentVariable("CDRIVEMASTER_TEST_TEMP", envRoot);
        try
        {
            var detector = new StubAppDetector(new DetectionResult(true, string.Empty, "FallbackDetector", "No base path."));
            var rule = new CleanupRule
            {
                AppName = "Windows 系统缓存",
                Description = "Env rule",
                DefaultAction = CleanupAction.DeleteToRecycleBin,
                Targets =
                {
                    new TargetRule
                    {
                        BaseFolder = "%CDRIVEMASTER_TEST_TEMP%",
                        Kind = "Cache",
                        RiskLevel = RiskLevel.SafeAuto
                    }
                }
            };

            var provider = new GenericRuleProvider(rule, detector, new BucketBuilder());

            var buckets = provider.GetBuckets();

            buckets.Should().HaveCount(1);
            buckets[0].RootPath.Should().Be(envRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDRIVEMASTER_TEST_TEMP", null);
        }
    }
}
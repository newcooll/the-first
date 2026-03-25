using System.IO;
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
}
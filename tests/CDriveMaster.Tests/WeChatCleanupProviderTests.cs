using System;
using System.IO;
using System.Linq;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Providers;
using CDriveMaster.Core.Services;
using CDriveMaster.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace CDriveMaster.Tests;

public sealed class WeChatCleanupProviderTests
{
    [Fact]
    public void GetBuckets_should_emit_cache_as_safe_auto_and_applet_as_safe_with_preview()
    {
        using var sandbox = new TempSandbox("wechat-provider");
        var root = sandbox.CreateDirectory("WeChat Files");

        _ = sandbox.CreateFile(Path.Combine("WeChat Files", "wxid_123", "FileStorage", "Cache", "a.tmp"), "cache");
        _ = sandbox.CreateFile(Path.Combine("WeChat Files", "wxid_123", "FileStorage", "Cache", "b.tmp"), "cache");
        _ = sandbox.CreateFile(Path.Combine("WeChat Files", "Applet", "pkg_001", "cache.bin"), "applet-cache");

        var detector = new StubAppDetector(root);
        var bucketBuilder = new BucketBuilder();
        var provider = new WeChatCleanupProvider(detector, bucketBuilder);

        var buckets = provider.GetBuckets();

        buckets.Should().NotBeEmpty();

        var cacheBucket = buckets.Single(b => b.Category == "Cache");
        cacheBucket.RiskLevel.Should().Be(RiskLevel.SafeAuto);
        cacheBucket.SuggestedAction.Should().Be(CleanupAction.DeleteToRecycleBin);
        cacheBucket.RootPath.Should().EndWith(Path.Combine("wxid_123", "FileStorage", "Cache"));
        cacheBucket.Entries.Should().NotBeEmpty();

        var appletBucket = buckets.Single(b => b.Category == "Applet");
        appletBucket.RiskLevel.Should().Be(RiskLevel.SafeWithPreview);
        appletBucket.SuggestedAction.Should().Be(CleanupAction.DeleteToRecycleBin);
        appletBucket.RootPath.Should().EndWith("Applet");
        appletBucket.Entries.Should().NotBeEmpty();

        buckets.Should().OnlyContain(b => b.RootPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetBuckets_should_not_emit_any_bucket_when_detector_returns_not_found()
    {
        var detector = new StubAppDetector(
            new CDriveMaster.Core.Interfaces.DetectionResult(false, null, "TestStub", "No WeChat root"));

        var bucketBuilder = new BucketBuilder();
        var provider = new WeChatCleanupProvider(detector, bucketBuilder);

        var buckets = provider.GetBuckets();

        buckets.Should().BeEmpty();
    }
}

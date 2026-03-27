using System.Collections.Generic;
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

    [Fact]
    public async Task ProbeAsync_WithHeuristicHint_ShouldReachDeepMatchThroughOneUnmatchedLevel()
    {
        using var sandbox = new TempSandbox("generic-provider-heuristic-deep");
        string heuristicRoot = sandbox.CreateDirectory("Roaming");
        _ = sandbox.CreateDirectory("Roaming", "ByteDance", "JianyingPro", "cache");
        _ = sandbox.CreateFile(Path.Combine("Roaming", "ByteDance", "JianyingPro", "cache", "model.bin"), new string('x', 4096));

        Environment.SetEnvironmentVariable("CDM_HEURISTIC_PARENT", heuristicRoot);
        try
        {
            var rule = new CleanupRule
            {
                AppName = "Jianying",
                Description = "Heuristic deep rule",
                DefaultAction = CleanupAction.DeleteToRecycleBin,
                Targets =
                {
                    new TargetRule { BaseFolder = "%CDM_HEURISTIC_PARENT%", Kind = "MediaCache", RiskLevel = RiskLevel.SafeWithPreview }
                },
                FastScan = new FastScanHint
                {
                    Category = "MediaCache",
                    MinSizeThreshold = 1,
                    IsExperimental = true,
                    HeuristicSearchHints = new[]
                    {
                        new HeuristicSearchHint
                        {
                            Parent = "%CDM_HEURISTIC_PARENT%",
                            AppTokens = new[] { "jianying", "jianyingpro", "capcut" },
                            CacheTokens = new[] { "cache" },
                            FileMarkersAny = new[] { "model" },
                            MaxDepth = 3,
                            ScoreThreshold = 5,
                            MinCandidateBytes = 1
                        }
                    }
                }
            };

            var provider = new GenericRuleProvider(
                rule,
                new StubAppDetector(new DetectionResult(true, string.Empty, "FallbackDetector", "test")),
                new BucketBuilder());

            var finding = await provider.ProbeAsync(rule, CancellationToken.None);

            finding.Should().NotBeNull();
            finding!.PrimaryPath.Should().Contain("JianyingPro");
            finding.Trace.VerifiedDirectories.Should().NotBeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDM_HEURISTIC_PARENT", null);
        }
    }

    [Fact]
    public async Task ProbeAsync_WithManyHeuristicCandidates_ShouldCapTraceCollections()
    {
        using var sandbox = new TempSandbox("generic-provider-trace-cap");
        string heuristicRoot = sandbox.CreateDirectory("Candidates");
        for (int i = 0; i < 260; i++)
        {
            _ = sandbox.CreateDirectory("Candidates", $"Dir_{i:D3}");
        }

        Environment.SetEnvironmentVariable("CDM_TRACE_PARENT", heuristicRoot);
        try
        {
            var rule = new CleanupRule
            {
                AppName = "Quark",
                Description = "Trace cap rule",
                DefaultAction = CleanupAction.DeleteToRecycleBin,
                Targets =
                {
                    new TargetRule { BaseFolder = "%CDM_TRACE_PARENT%", Kind = "BrowserCache", RiskLevel = RiskLevel.SafeWithPreview }
                },
                FastScan = new FastScanHint
                {
                    Category = "BrowserCache",
                    MinSizeThreshold = 1,
                    IsExperimental = true,
                    HeuristicSearchHints = new[]
                    {
                        new HeuristicSearchHint
                        {
                            Parent = "%CDM_TRACE_PARENT%",
                            AppTokens = new[] { "quark" },
                            CacheTokens = new[] { "cache" },
                            FileMarkersAny = Array.Empty<string>(),
                            MaxDepth = 2,
                            ScoreThreshold = 5,
                            MinCandidateBytes = 1
                        }
                    }
                }
            };

            var provider = new GenericRuleProvider(
                rule,
                new StubAppDetector(new DetectionResult(true, string.Empty, "FallbackDetector", "test")),
                new BucketBuilder());

            var finding = await provider.ProbeAsync(rule, CancellationToken.None);

            finding.Should().NotBeNull();
            finding!.Trace.CandidateDirectories.Count.Should().BeLessOrEqualTo(201);
            finding.Trace.RejectReasons.Count.Should().BeLessOrEqualTo(201);
            finding.Trace.CandidateDirectories.Should().Contain("……更多记录已省略");
            finding.Trace.RejectReasons.Should().Contain("……更多记录已省略");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDM_TRACE_PARENT", null);
        }
    }

    [Fact]
    public async Task ProbeAsync_WithLargeHeuristicCandidate_ShouldReturnApproximateSizeQuickly()
    {
        using var sandbox = new TempSandbox("generic-provider-large-heuristic");
        string cacheDir = sandbox.CreateDirectory("Roaming", "Quark", "quark-cloud-drive", "cache");
        string largeFile = Path.Combine(cacheDir, "video-cache.bin");
        await using (var stream = File.Create(largeFile))
        {
            stream.SetLength(160L * 1024L * 1024L);
        }

        Environment.SetEnvironmentVariable("CDM_LARGE_HEURISTIC_PARENT", sandbox.Combine("Roaming"));
        try
        {
            var rule = new CleanupRule
            {
                AppName = "Quark",
                Description = "Large heuristic rule",
                DefaultAction = CleanupAction.DeleteToRecycleBin,
                Targets =
                {
                    new TargetRule { BaseFolder = "%CDM_LARGE_HEURISTIC_PARENT%", Kind = "BrowserCache", RiskLevel = RiskLevel.SafeWithPreview }
                },
                FastScan = new FastScanHint
                {
                    Category = "BrowserCache",
                    MinSizeThreshold = 20L * 1024L * 1024L,
                    IsExperimental = true,
                    HeuristicSearchHints = new[]
                    {
                        new HeuristicSearchHint
                        {
                            Parent = "%CDM_LARGE_HEURISTIC_PARENT%",
                            AppTokens = new[] { "quark", "quark-cloud-drive" },
                            CacheTokens = new[] { "cache" },
                            FileMarkersAny = new[] { "video-cache" },
                            MaxDepth = 3,
                            ScoreThreshold = 5,
                            MinCandidateBytes = 1
                        }
                    }
                }
            };

            var provider = new GenericRuleProvider(
                rule,
                new StubAppDetector(new DetectionResult(true, string.Empty, "FallbackDetector", "test")),
                new BucketBuilder());

            var finding = await provider.ProbeAsync(rule, CancellationToken.None);

            finding.Should().NotBeNull();
            finding!.IsHotspot.Should().BeTrue();
            finding.IsExactSize.Should().BeFalse();
            finding.DisplaySize.Should().StartWith("> ");
            finding.PrimaryPath.Should().Contain("quark-cloud-drive");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDM_LARGE_HEURISTIC_PARENT", null);
        }
    }

    [Fact]
    public async Task ProbeAsync_WithDirectTargetSeed_ShouldFindDeepCacheEvenWhenParentDepthIsShallow()
    {
        using var sandbox = new TempSandbox("generic-provider-seeded-target");
        string parentRoot = sandbox.CreateDirectory("LocalAppData");
        string deepCache = sandbox.CreateDirectory("LocalAppData", "JianyingPro", "User Data", "Cache");
        _ = sandbox.CreateFile(Path.Combine("LocalAppData", "JianyingPro", "User Data", "Cache", "model.bin"), new string('x', 4096));

        Environment.SetEnvironmentVariable("CDM_SEED_PARENT", parentRoot);
        try
        {
            var rule = new CleanupRule
            {
                AppName = "Jianying",
                Description = "Seeded target rule",
                DefaultAction = CleanupAction.DeleteToRecycleBin,
                Targets =
                {
                    new TargetRule { BaseFolder = "%CDM_SEED_PARENT%\\JianyingPro\\User Data\\Cache", Kind = "MediaCache", RiskLevel = RiskLevel.SafeWithPreview }
                },
                FastScan = new FastScanHint
                {
                    Category = "MediaCache",
                    MinSizeThreshold = 1,
                    IsExperimental = true,
                    HeuristicSearchHints = new[]
                    {
                        new HeuristicSearchHint
                        {
                            Parent = "%CDM_SEED_PARENT%",
                            AppTokens = new[] { "jianyingpro" },
                            CacheTokens = new[] { "cache" },
                            FileMarkersAny = new[] { "model" },
                            MaxDepth = 1,
                            ScoreThreshold = 3,
                            MinCandidateBytes = 1
                        }
                    }
                }
            };

            var provider = new GenericRuleProvider(
                rule,
                new StubAppDetector(new DetectionResult(true, string.Empty, "FallbackDetector", "test")),
                new BucketBuilder());

            var finding = await provider.ProbeAsync(rule, CancellationToken.None);

            finding.Should().NotBeNull();
            finding!.PrimaryPath.Should().Be(deepCache);
            finding.IsHotspot.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDM_SEED_PARENT", null);
        }
    }
}

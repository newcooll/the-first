using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CDriveMaster.Core.Interfaces;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;
using CDriveMaster.Tests.Fakes;
using CDriveMaster.Tests.Helpers;
using CDriveMaster.UI.ViewModels;
using FluentAssertions;

namespace CDriveMaster.Tests.ViewModels;

public sealed class BasicScanDashboardViewModelTests
{
    [Fact]
    public void ViewDetailsCommand_ShouldSummarizeLargeTracePayload()
    {
        var dialog = new FakeDialogService();
        var vm = CreateViewModel(dialog);
        var finding = new FastScanFinding
        {
            AppId = "Quark",
            IsHotspot = false,
            Trace = new ProbeTraceInfo(
                3,
                new List<string> { "DisplayName:Quark", "LocalTrace:%APPDATA%\\Quark" },
                BuildEntries(@"C:\Candidates", 40),
                BuildEntries(@"C:\Verified", 18),
                BuildEntries(@"C:\Rejected", 24),
                BuildEntries("reason", 30),
                BuildEntries("history", 35),
                "检测到可疑路径，但未达热点阈值")
        };

        vm.ViewDetailsCommand.Execute(finding);

        dialog.WasShowInfoCalled.Should().BeTrue();
        dialog.LastMessage.Should().Contain("最终结论: 检测到可疑路径，但未达热点阈值");
        dialog.LastMessage.Should().Contain("共 40 项，仅显示前");
        dialog.LastMessage.Should().Contain("共 35 项，仅显示前");
        dialog.LastMessage.Length.Should().BeLessThan(5000);
    }

    [Fact]
    public void BuildReverseAttributedHotspots_ShouldMapLargeFilesToKnownAppRules()
    {
        using var sandbox = new TempSandbox("vm-reverse-hotspot");
        string quarkCache = sandbox.CreateDirectory("LocalAppData", "Quark", "Cache", "Data");
        string jianyingCache = sandbox.CreateDirectory("LocalAppData", "JianyingPro", "User Data", "Cache");

        var files = new[]
        {
            new LargeFileItem("quark-video.bin", Path.Combine(quarkCache, "quark-video.bin"), 320L * 1024L * 1024L, DateTime.Now),
            new LargeFileItem("jianying-draft.bin", Path.Combine(jianyingCache, "jianying-draft.bin"), 540L * 1024L * 1024L, DateTime.Now)
        };

        var rules = new[]
        {
            new CleanupRule
            {
                AppName = "Quark",
                AppMatchKeywords = new[] { "Quark" },
                DefaultAction = CleanupAction.DeleteToRecycleBin,
                Targets =
                {
                    new TargetRule { BaseFolder = sandbox.Combine("LocalAppData", "Quark"), Kind = "BrowserCache", RiskLevel = RiskLevel.SafeWithPreview }
                },
                FastScan = new FastScanHint
                {
                    Category = "BrowserCache",
                    MinSizeThreshold = 20L * 1024L * 1024L,
                    HeuristicSearchHints = new[]
                    {
                        new HeuristicSearchHint
                        {
                            Parent = sandbox.Combine("LocalAppData"),
                            AppTokens = new[] { "quark", "quark-cloud-drive" },
                            CacheTokens = new[] { "cache", "db" },
                            FileMarkersAny = new[] { "quark", "cache" }
                        }
                    }
                }
            },
            new CleanupRule
            {
                AppName = "Jianying",
                AppMatchKeywords = new[] { "Jianying", "JianyingPro" },
                DefaultAction = CleanupAction.DeleteToRecycleBin,
                Targets =
                {
                    new TargetRule { BaseFolder = sandbox.Combine("LocalAppData", "JianyingPro", "User Data", "Cache"), Kind = "MediaCache", RiskLevel = RiskLevel.SafeWithPreview }
                },
                FastScan = new FastScanHint
                {
                    Category = "MediaCache",
                    MinSizeThreshold = 20L * 1024L * 1024L,
                    HeuristicSearchHints = new[]
                    {
                        new HeuristicSearchHint
                        {
                            Parent = sandbox.Combine("LocalAppData"),
                            AppTokens = new[] { "jianying", "jianyingpro", "capcut" },
                            CacheTokens = new[] { "cache", "draft" },
                            FileMarkersAny = new[] { "draft", "model" }
                        }
                    }
                }
            }
        };

        var hotspots = BasicScanDashboardViewModel.BuildReverseAttributedHotspots(files, rules);

        hotspots.Should().HaveCount(2);
        hotspots.Should().Contain(item => item.AppId == "Quark" && item.IsHotspot && item.TotalSizeBytes == 320L * 1024L * 1024L);
        hotspots.Should().Contain(item => item.AppId == "Jianying" && item.IsHotspot && item.TotalSizeBytes == 540L * 1024L * 1024L);
    }

    [Fact]
    public void BuildReverseAttributedHotspots_ShouldIgnoreFilesBelowReverseAttributionThreshold()
    {
        using var sandbox = new TempSandbox("vm-reverse-threshold");
        string quarkCache = sandbox.CreateDirectory("LocalAppData", "Quark", "Cache");
        var files = new[]
        {
            new LargeFileItem("quark-small.bin", Path.Combine(quarkCache, "quark-small.bin"), 64L * 1024L * 1024L, DateTime.Now)
        };

        var rules = new[]
        {
            new CleanupRule
            {
                AppName = "Quark",
                AppMatchKeywords = new[] { "Quark" },
                DefaultAction = CleanupAction.DeleteToRecycleBin,
                Targets =
                {
                    new TargetRule { BaseFolder = sandbox.Combine("LocalAppData", "Quark"), Kind = "BrowserCache", RiskLevel = RiskLevel.SafeWithPreview }
                },
                FastScan = new FastScanHint
                {
                    Category = "BrowserCache",
                    MinSizeThreshold = 20L * 1024L * 1024L
                }
            }
        };

        var hotspots = BasicScanDashboardViewModel.BuildReverseAttributedHotspots(files, rules);

        hotspots.Should().BeEmpty();
    }

    private static BasicScanDashboardViewModel CreateViewModel(FakeDialogService dialog)
    {
        return new BasicScanDashboardViewModel(
            new LargeFileScanner(),
            new RuleCatalog(Array.Empty<IAppDetector>(), new BucketBuilder()),
            new AppPresenceDetector(),
            new FakeCleanupPipeline(),
            new AuditLogExporter(),
            dialog);
    }

    private static List<string> BuildEntries(string prefix, int count)
    {
        var values = new List<string>();
        for (int i = 0; i < count; i++)
        {
            values.Add($"{prefix}_{i:D2}_{new string('x', 40)}");
        }

        return values;
    }

    private sealed class FakeCleanupPipeline : ICleanupPipeline
    {
        public IReadOnlyList<BucketResult> Execute(IReadOnlyList<CleanupBucket> buckets, bool apply)
        {
            return Array.Empty<BucketResult>();
        }

        public Task<IReadOnlyList<BucketResult>> ExecuteAsync(
            IReadOnlyList<CleanupBucket> buckets,
            bool apply,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<BucketResult>>(Array.Empty<BucketResult>());
        }

        public BucketResult ExecuteEntries(CleanupBucket parentBucket, IEnumerable<CleanupEntry> entriesToApply, bool apply)
        {
            return new BucketResult(
                parentBucket,
                ExecutionStatus.Skipped,
                0,
                0,
                0,
                0,
                Array.Empty<AuditLogItem>());
        }
    }
}

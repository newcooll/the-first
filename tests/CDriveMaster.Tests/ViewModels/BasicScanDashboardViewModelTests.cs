using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CDriveMaster.Core.Interfaces;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;
using CDriveMaster.Tests.Fakes;
using CDriveMaster.Tests.Helpers;
using CDriveMaster.UI.Services;
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
    public void RefreshCDriveSpaceStatus_ShouldPopulateDriveSummary()
    {
        var vm = CreateViewModel(new FakeDialogService());

        vm.RefreshCDriveSpaceStatus();

        vm.CDriveUsageText.Should().NotBeNullOrWhiteSpace();
        vm.CDriveTotalBytes.Should().BeGreaterThan(0);
        vm.CDriveFreeBytes.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task ExecuteCleanSelectedCommand_ShouldExecuteSafeAutoBucketsWithoutPreview()
    {
        using var sandbox = new TempSandbox("vm-clean-selected-direct");
        string cacheRoot = sandbox.CreateDirectory("LocalAppData", "Temp", "Safe");
        string firstFile = Path.Combine(cacheRoot, "a.bin");
        string secondFile = Path.Combine(cacheRoot, "b.bin");
        var bucket = CreateBucket(cacheRoot, firstFile, secondFile) with
        {
            AllowedRoots = new[] { firstFile, secondFile }
        };

        var dialog = new FakeDialogService();
        var preview = new FakePreviewDialogService();
        var pipeline = new FakeCleanupPipeline();
        var vm = CreateViewModel(dialog, pipeline, preview);
        var group = new BasicScanGroup
        {
            GroupId = "safe-items",
            Title = "安全项",
            Description = "可自动清理"
        };
        group.Items.Add(new BasicScanItem
        {
            Id = "safe-item-1",
            Title = "Temp Cache",
            Description = "Safe temp files",
            FullPath = firstFile,
            SizeBytes = bucket.EstimatedSizeBytes,
            RiskLevel = RiskLevel.SafeAuto,
            ActionType = BasicScanActionType.CleanSelected,
            IsSelectable = true,
            IsSelected = true,
            OriginalBucket = bucket
        });
        vm.ScanGroups.Add(group);

        await vm.ExecuteCleanSelectedCommand.ExecuteAsync(null);

        dialog.WasConfirmCalled.Should().BeTrue();
        preview.WasShowPreviewCalled.Should().BeFalse();
        dialog.WasShowInfoCalled.Should().BeTrue();
        pipeline.ExecuteEntriesCalls.Should().ContainSingle();
        pipeline.ExecuteEntriesCalls[0].Entries.Should().HaveCount(2);
        pipeline.ExecuteEntriesCalls[0].AllowTrustedExactFileFastPath.Should().BeTrue();
        vm.StatusText.Should().Be("清理完成");
    }

    [Fact]
    public async Task ExecuteCleanSelectedCommand_ShouldAllowSelectedLargeFilesWithPreview()
    {
        using var sandbox = new TempSandbox("vm-clean-selected-large-file");
        string rootPath = sandbox.CreateDirectory("DeepScan", "LargeFiles");
        string filePath = Path.Combine(rootPath, "movie.mkv");
        var bucket = CreateLargeFileBucket(filePath, 900L * 1024L * 1024L);

        var dialog = new FakeDialogService();
        var preview = new FakePreviewDialogService
        {
            SelectedEntries = bucket.Entries
        };
        var pipeline = new FakeCleanupPipeline();
        var vm = CreateViewModel(dialog, pipeline, preview);
        var group = new BasicScanGroup
        {
            GroupId = "large-file-radar",
            Title = "大文件雷达",
            Description = "Top 20"
        };
        group.Items.Add(new BasicScanItem
        {
            Id = "large-item-1",
            Title = "movie.mkv",
            Description = "大文件分析结果",
            FullPath = filePath,
            SizeBytes = bucket.EstimatedSizeBytes,
            RiskLevel = RiskLevel.SafeWithPreview,
            ActionType = BasicScanActionType.CleanSelected,
            IsSelectable = true,
            IsSelected = true,
            OriginalBucket = bucket
        });
        vm.ScanGroups.Add(group);

        vm.ExecuteCleanSelectedCommand.CanExecute(null).Should().BeTrue();

        await vm.ExecuteCleanSelectedCommand.ExecuteAsync(null);

        preview.WasShowPreviewCalled.Should().BeTrue();
        pipeline.ExecuteEntriesCalls.Should().ContainSingle();
        pipeline.ExecuteEntriesCalls[0].Entries.Should().ContainSingle(entry =>
            string.Equals(entry.Path, filePath, StringComparison.OrdinalIgnoreCase));
        pipeline.ExecuteEntriesCalls[0].AllowTrustedExactFileFastPath.Should().BeTrue();
    }

    [Fact]
    public void BuildCleanupCompletionSummary_ShouldDescribeRecycleBinAsMovedNotReleased()
    {
        using var sandbox = new TempSandbox("vm-cleanup-summary-recycle");
        string filePath = Path.Combine(sandbox.CreateDirectory("cache"), "a.bin");
        var bucket = CreateLargeFileBucket(filePath, 256L * 1024L * 1024L);

        string summary = BasicScanDashboardViewModel.BuildCleanupCompletionSummary(
            new[] { bucket },
            256L * 1024L * 1024L,
            successCount: 1,
            skippedCount: 0,
            failedCount: 0);

        summary.Should().Contain("已移出原位置");
        summary.Should().Contain("回收站");
        summary.Should().NotContain("成功释放");
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
        hotspots.Should().OnlyContain(item => item.OriginalBucket != null);
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

    [Fact]
    public void BuildReverseAttributedHotspots_ShouldMatchNormalizedKeywordPathsUnderSearchParent()
    {
        using var sandbox = new TempSandbox("vm-reverse-normalized-path");
        string quarkCache = sandbox.CreateDirectory("RoamingAppData", "Quark Cloud Drive", "PDF Cache", "BlobStore");
        string filePath = Path.Combine(quarkCache, "00001.bin");

        var files = new[]
        {
            new LargeFileItem("00001.bin", filePath, 260L * 1024L * 1024L, DateTime.Now)
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
                ResidualFingerprints = new List<ResidualFingerprint>
                {
                    new()
                    {
                        Parent = sandbox.Combine("RoamingAppData"),
                        PathKeywords = new List<string> { "quark-cloud-drive", "pdf-cache" }
                    }
                },
                FastScan = new FastScanHint
                {
                    Category = "BrowserCache",
                    MinSizeThreshold = 20L * 1024L * 1024L,
                    SearchHints = new[]
                    {
                        new SearchHint
                        {
                            Parent = sandbox.Combine("RoamingAppData"),
                            DirectoryKeywords = new[] { "quark-cloud-drive", "pdf-cache" },
                            ChildMarkersAny = new[] { "blobstore" }
                        }
                    }
                }
            }
        };

        var hotspots = BasicScanDashboardViewModel.BuildReverseAttributedHotspots(files, rules);

        hotspots.Should().ContainSingle();
        hotspots[0].AppId.Should().Be("Quark");
        hotspots[0].PrimaryPath.Should().Be(sandbox.Combine("RoamingAppData", "Quark Cloud Drive"));
        hotspots[0].IsHotspot.Should().BeTrue();
    }

    [Fact]
    public void BuildReverseAttributedHotspots_ShouldAggregateSameAppFilesAndExcludeUnsafeCleanupEntries()
    {
        using var sandbox = new TempSandbox("vm-reverse-aggregate-safe");
        string cacheA = sandbox.CreateDirectory("LocalAppData", "Xunlei", "Cache", "PartA");
        string cacheB = sandbox.CreateDirectory("LocalAppData", "Xunlei", "Cache", "PartB");

        var files = new[]
        {
            new LargeFileItem("video-a.bin", Path.Combine(cacheA, "video-a.bin"), 320L * 1024L * 1024L, DateTime.Now),
            new LargeFileItem("module.dll", Path.Combine(cacheB, "module.dll"), 380L * 1024L * 1024L, DateTime.Now)
        };

        var rules = new[]
        {
            new CleanupRule
            {
                AppName = "Xunlei",
                AppMatchKeywords = new[] { "Xunlei", "迅雷" },
                DefaultAction = CleanupAction.DeleteToRecycleBin,
                Targets =
                {
                    new TargetRule { BaseFolder = sandbox.Combine("LocalAppData", "Xunlei"), Kind = "DownloadCache", RiskLevel = RiskLevel.SafeWithPreview }
                },
                FastScan = new FastScanHint
                {
                    Category = "DownloadCache",
                    MinSizeThreshold = 20L * 1024L * 1024L
                }
            }
        };

        var hotspots = BasicScanDashboardViewModel.BuildReverseAttributedHotspots(files, rules);

        hotspots.Should().ContainSingle();
        hotspots[0].AppId.Should().Be("Xunlei");
        hotspots[0].TotalSizeBytes.Should().Be((320L + 380L) * 1024L * 1024L);
        hotspots[0].OriginalBucket.Should().NotBeNull();
        hotspots[0].OriginalBucket!.Entries.Should().ContainSingle();
        hotspots[0].OriginalBucket!.Entries[0].Path.Should().EndWith("video-a.bin");
    }

    [Fact]
    public void BuildReverseAttributedHotspots_ShouldIgnoreUnsafeOnlyFiles()
    {
        using var sandbox = new TempSandbox("vm-reverse-unsafe-only");
        string moduleRoot = sandbox.CreateDirectory("LocalAppData", "Xunlei", "Modules");

        var files = new[]
        {
            new LargeFileItem("core.dll", Path.Combine(moduleRoot, "core.dll"), 620L * 1024L * 1024L, DateTime.Now)
        };

        var rules = new[]
        {
            new CleanupRule
            {
                AppName = "Xunlei",
                AppMatchKeywords = new[] { "Xunlei", "迅雷" },
                DefaultAction = CleanupAction.DeleteToRecycleBin,
                Targets =
                {
                    new TargetRule { BaseFolder = sandbox.Combine("LocalAppData", "Xunlei"), Kind = "DownloadCache", RiskLevel = RiskLevel.SafeWithPreview }
                },
                FastScan = new FastScanHint
                {
                    Category = "DownloadCache",
                    MinSizeThreshold = 20L * 1024L * 1024L
                }
            }
        };

        var hotspots = BasicScanDashboardViewModel.BuildReverseAttributedHotspots(files, rules);

        hotspots.Should().BeEmpty();
    }

    [Fact]
    public void BuildReverseAttributedHotspots_ShouldNotClassifyGenericCachePathWithoutAppIdentity()
    {
        using var sandbox = new TempSandbox("vm-reverse-generic-cache");
        string genericCache = sandbox.CreateDirectory("LocalAppData", "Packages", "Shared", "GPUCache");

        var files = new[]
        {
            new LargeFileItem("shared-video.bin", Path.Combine(genericCache, "shared-video.bin"), 780L * 1024L * 1024L, DateTime.Now)
        };

        var rules = new[]
        {
            new CleanupRule
            {
                AppName = "Youku",
                AppMatchKeywords = new[] { "Youku" },
                DefaultAction = CleanupAction.DeleteToRecycleBin,
                FastScan = new FastScanHint
                {
                    Category = "VideoCache",
                    HeuristicSearchHints = new HeuristicSearchHint[]
                    {
                        new HeuristicSearchHint
                        {
                            Parent = sandbox.Combine("LocalAppData"),
                            AppTokens = new[] { "youku" },
                            CacheTokens = new[] { "cache", "gpucache" },
                            FileMarkersAny = new[] { "video" }
                        }
                    }
                },
                ResidualFingerprints = new List<ResidualFingerprint>
                {
                    new()
                    {
                        Parent = sandbox.Combine("LocalAppData"),
                        PathKeywords = new List<string> { "youku", "cache", "gpucache" }
                    }
                }
            },
            new CleanupRule
            {
                AppName = "Xunlei",
                AppMatchKeywords = new[] { "Xunlei", "Thunder" },
                DefaultAction = CleanupAction.DeleteToRecycleBin,
                FastScan = new FastScanHint
                {
                    Category = "DownloadCache",
                    HeuristicSearchHints = new HeuristicSearchHint[]
                    {
                        new HeuristicSearchHint
                        {
                            Parent = sandbox.Combine("LocalAppData"),
                            AppTokens = new[] { "xunlei", "thunder" },
                            CacheTokens = new[] { "cache", "gpucache" },
                            FileMarkersAny = new[] { "video" }
                        }
                    }
                },
                ResidualFingerprints = new List<ResidualFingerprint>
                {
                    new()
                    {
                        Parent = sandbox.Combine("LocalAppData"),
                        PathKeywords = new List<string> { "xunlei", "thunder", "cache", "gpucache" }
                    }
                }
            }
        };

        var hotspots = BasicScanDashboardViewModel.BuildReverseAttributedHotspots(files, rules);

        hotspots.Should().BeEmpty();
    }

    [Fact]
    public void CombineAppHotspots_ShouldKeepLargerReverseAttributedHotspotWhenPrimaryFindingIsWeaker()
    {
        var reverseAttributed = new FastScanFinding
        {
            AppId = "Jianying",
            SizeBytes = 640L * 1024L * 1024L,
            Category = "MediaCache",
            PrimaryPath = @"C:\Users\Test\AppData\Local\ByteDance\JianyingPro\Component Store",
            SourcePath = @"C:\Users\Test\AppData\Local\ByteDance\JianyingPro\Component Store\chunks\0001.bin",
            IsExactSize = true,
            DisplaySize = "640 MB",
            IsHotspot = true,
            IsHeuristicMatch = true
        };

        var weakProbeFinding = new FastScanFinding
        {
            AppId = "Jianying",
            SizeBytes = 32L * 1024L * 1024L,
            Category = "MediaCache",
            PrimaryPath = @"C:\Users\Test\AppData\Local\JianyingPro\User Data\Cache",
            SourcePath = @"C:\Users\Test\AppData\Local\JianyingPro\User Data\Cache\data_0",
            IsExactSize = true,
            DisplaySize = "32 MB",
            IsHotspot = false,
            IsHeuristicMatch = false
        };

        var combined = BasicScanDashboardViewModel.CombineAppHotspots(
            new[] { weakProbeFinding },
            new[] { reverseAttributed });

        combined.Should().ContainSingle();
        combined[0].TotalSizeBytes.Should().Be(640L * 1024L * 1024L);
        combined[0].PrimaryPath.Should().Be(reverseAttributed.PrimaryPath);
        combined[0].IsHotspot.Should().BeTrue();
    }

    [Fact]
    public void FilterCommonAppCaches_ShouldKeepUnrelatedPathCandidateWhenPreciseHotspotUsesDifferentPath()
    {
        var commonCandidates = new[]
        {
            new FastScanFinding
            {
                AppId = "Youku",
                SizeBytes = 700L * 1024L * 1024L,
                PrimaryPath = @"C:\Users\Test\AppData\Local\Shared\Cache",
                SourcePath = @"C:\Users\Test\AppData\Local\Shared\Cache\a.bin",
                IsExactSize = true,
                DisplaySize = "700 MB",
                IsHotspot = true,
                IsHeuristicMatch = true
            },
            new FastScanFinding
            {
                AppId = "Quark",
                SizeBytes = 900L * 1024L * 1024L,
                PrimaryPath = @"C:\Users\Test\AppData\Local\AnotherShared\Cache",
                SourcePath = @"C:\Users\Test\AppData\Local\AnotherShared\Cache\b.bin",
                IsExactSize = true,
                DisplaySize = "900 MB",
                IsHotspot = true,
                IsHeuristicMatch = true
            }
        };

        var preciseHotspots = new[]
        {
            new FastScanFinding
            {
                AppId = "Youku",
                SizeBytes = 256L * 1024L * 1024L,
                PrimaryPath = @"C:\Users\Test\AppData\Local\Youku\Cache",
                SourcePath = @"C:\Users\Test\AppData\Local\Youku\Cache\c.bin",
                IsExactSize = true,
                DisplaySize = "256 MB",
                IsHotspot = true,
                IsHeuristicMatch = false
            }
        };

        var filtered = BasicScanDashboardViewModel.FilterCommonAppCaches(commonCandidates, preciseHotspots);

        filtered.Should().HaveCount(2);
        filtered.Select(item => item.PrimaryPath).Should().BeEquivalentTo(new[]
        {
            @"C:\Users\Test\AppData\Local\Shared\Cache",
            @"C:\Users\Test\AppData\Local\AnotherShared\Cache"
        });
        filtered.Should().OnlyContain(item => item.AppId.StartsWith("common-cache:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AggregateCommonAppCachesByPath_ShouldMergeSamePathAcrossDifferentCandidateApps()
    {
        string sharedPath = @"C:\Users\Test\AppData\Local\Shared\Cache";
        var findings = new[]
        {
            new FastScanFinding
            {
                AppId = "Youku",
                SizeBytes = 700L * 1024L * 1024L,
                Category = "VideoCache",
                PrimaryPath = sharedPath,
                SourcePath = Path.Combine(sharedPath, "a.bin"),
                IsExactSize = true,
                DisplaySize = "700 MB",
                IsHotspot = true,
                IsHeuristicMatch = true
            },
            new FastScanFinding
            {
                AppId = "Xunlei",
                SizeBytes = 600L * 1024L * 1024L,
                Category = "DownloadCache",
                PrimaryPath = sharedPath,
                SourcePath = Path.Combine(sharedPath, "b.bin"),
                IsExactSize = true,
                DisplaySize = "600 MB",
                IsHotspot = true,
                IsHeuristicMatch = true
            },
            new FastScanFinding
            {
                AppId = "iQIYI",
                SizeBytes = 500L * 1024L * 1024L,
                Category = "VideoCache",
                PrimaryPath = sharedPath,
                SourcePath = Path.Combine(sharedPath, "c.bin"),
                IsExactSize = true,
                DisplaySize = "500 MB",
                IsHotspot = true,
                IsHeuristicMatch = true
            }
        };

        var aggregated = BasicScanDashboardViewModel.AggregateCommonAppCachesByPath(findings);

        aggregated.Should().ContainSingle();
        aggregated[0].PrimaryPath.Should().Be(sharedPath);
        aggregated[0].TotalSizeBytes.Should().Be((700L + 600L + 500L) * 1024L * 1024L);
        aggregated[0].Category.Should().Contain("Youku");
        aggregated[0].Category.Should().Contain("Xunlei");
        aggregated[0].Category.Should().Contain("iQIYI");
        aggregated[0].AppId.Should().StartWith("common-cache:");
    }

    [Fact]
    public void CollapseCommonAppCachesForDisplay_ShouldMergeAllPathsIntoSingleCard()
    {
        var findings = new[]
        {
            new FastScanFinding
            {
                AppId = "Youku",
                SizeBytes = 700L * 1024L * 1024L,
                Category = "VideoCache",
                PrimaryPath = @"C:\Users\Test\AppData\Local\Shared\Cache",
                SourcePath = @"C:\Users\Test\AppData\Local\Shared\Cache\a.bin",
                IsExactSize = true,
                DisplaySize = "700 MB",
                IsHotspot = true,
                IsHeuristicMatch = true,
                OriginalBucket = new CleanupBucket(
                    BucketId: "common-a",
                    Category: "VideoCache",
                    RootPath: @"C:\Users\Test\AppData\Local\Shared\Cache",
                    AppName: "Youku",
                    RiskLevel: RiskLevel.SafeWithPreview,
                    SuggestedAction: CleanupAction.DeleteToRecycleBin,
                    Description: "A",
                    EstimatedSizeBytes: 700L * 1024L * 1024L,
                    Entries: new List<CleanupEntry>
                    {
                        new(@"C:\Users\Test\AppData\Local\Shared\Cache\a.bin", false, 700L * 1024L * 1024L, DateTime.UtcNow, "VideoCache")
                    }.AsReadOnly())
            },
            new FastScanFinding
            {
                AppId = "Xunlei",
                SizeBytes = 500L * 1024L * 1024L,
                Category = "DownloadCache",
                PrimaryPath = @"C:\Users\Test\AppData\Local\AnotherShared\Cache",
                SourcePath = @"C:\Users\Test\AppData\Local\AnotherShared\Cache\b.bin",
                IsExactSize = true,
                DisplaySize = "500 MB",
                IsHotspot = true,
                IsHeuristicMatch = true,
                OriginalBucket = new CleanupBucket(
                    BucketId: "common-b",
                    Category: "DownloadCache",
                    RootPath: @"C:\Users\Test\AppData\Local\AnotherShared\Cache",
                    AppName: "Xunlei",
                    RiskLevel: RiskLevel.SafeWithPreview,
                    SuggestedAction: CleanupAction.DeleteToRecycleBin,
                    Description: "B",
                    EstimatedSizeBytes: 500L * 1024L * 1024L,
                    Entries: new List<CleanupEntry>
                    {
                        new(@"C:\Users\Test\AppData\Local\AnotherShared\Cache\b.bin", false, 500L * 1024L * 1024L, DateTime.UtcNow, "DownloadCache")
                    }.AsReadOnly())
            }
        };

        var collapsed = BasicScanDashboardViewModel.CollapseCommonAppCachesForDisplay(findings);

        collapsed.Should().ContainSingle();
        collapsed[0].AppId.Should().Be("常见应用深层残留");
        collapsed[0].Category.Should().Contain("Youku");
        collapsed[0].Category.Should().Contain("Xunlei");
        collapsed[0].PrimaryPath.Should().Be("已合并 2 个路径");
        collapsed[0].TotalSizeBytes.Should().Be((700L + 500L) * 1024L * 1024L);
        collapsed[0].OriginalBucket.Should().NotBeNull();
        var bucket = collapsed[0].OriginalBucket!;
        bucket.Entries.Should().HaveCount(2);
        bucket.AppName.Should().Be("常见应用深层残留");
    }

    [Fact]
    public void FilterCommonAppCaches_ShouldExcludeCommonCandidateWhenPreciseHotspotSharesSamePath()
    {
        string sharedPath = @"C:\Users\Test\AppData\Local\Shared\Cache";
        var commonCandidates = new[]
        {
            new FastScanFinding
            {
                AppId = "common-cache:shared",
                SizeBytes = 700L * 1024L * 1024L,
                Category = "候选应用: Youku / Xunlei",
                PrimaryPath = sharedPath,
                SourcePath = Path.Combine(sharedPath, "a.bin"),
                IsExactSize = true,
                DisplaySize = "700 MB",
                IsHotspot = true,
                IsHeuristicMatch = true
            }
        };

        var preciseHotspots = new[]
        {
            new FastScanFinding
            {
                AppId = "Youku",
                SizeBytes = 256L * 1024L * 1024L,
                Category = "VideoCache",
                PrimaryPath = sharedPath,
                SourcePath = Path.Combine(sharedPath, "confirmed.bin"),
                IsExactSize = true,
                DisplaySize = "256 MB",
                IsHotspot = true,
                IsHeuristicMatch = false
            }
        };

        var filtered = BasicScanDashboardViewModel.FilterCommonAppCaches(commonCandidates, preciseHotspots);

        filtered.Should().BeEmpty();
    }

    [Fact]
    public void AggregateFindingsByApp_ShouldMergeResidualHotspotsWithSameAppId()
    {
        var first = new FastScanFinding
        {
            AppId = "Xunlei",
            SizeBytes = 300L * 1024L * 1024L,
            Category = "ResidualCache",
            PrimaryPath = @"C:\Users\Test\AppData\Local\Xunlei\CacheA",
            SourcePath = @"C:\Users\Test\AppData\Local\Xunlei\CacheA\a.bin",
            IsExactSize = true,
            DisplaySize = "300 MB",
            IsHotspot = true,
            IsResidual = true,
            OriginalBucket = new CleanupBucket(
                BucketId: "xunlei-a",
                Category: "ResidualCache",
                RootPath: @"C:\Users\Test\AppData\Local\Xunlei\CacheA",
                AppName: "Xunlei",
                RiskLevel: RiskLevel.SafeWithPreview,
                SuggestedAction: CleanupAction.DeleteToRecycleBin,
                Description: "A",
                EstimatedSizeBytes: 300L * 1024L * 1024L,
                Entries: new List<CleanupEntry>
                {
                    new(@"C:\Users\Test\AppData\Local\Xunlei\CacheA\a.bin", false, 300L * 1024L * 1024L, DateTime.UtcNow, "ResidualCache")
                }.AsReadOnly())
        };

        var second = new FastScanFinding
        {
            AppId = "Xunlei",
            SizeBytes = 320L * 1024L * 1024L,
            Category = "ResidualCache",
            PrimaryPath = @"C:\Users\Test\AppData\Local\Xunlei\CacheB",
            SourcePath = @"C:\Users\Test\AppData\Local\Xunlei\CacheB\b.bin",
            IsExactSize = true,
            DisplaySize = "320 MB",
            IsHotspot = true,
            IsResidual = true,
            OriginalBucket = new CleanupBucket(
                BucketId: "xunlei-b",
                Category: "ResidualCache",
                RootPath: @"C:\Users\Test\AppData\Local\Xunlei\CacheB",
                AppName: "Xunlei",
                RiskLevel: RiskLevel.SafeWithPreview,
                SuggestedAction: CleanupAction.DeleteToRecycleBin,
                Description: "B",
                EstimatedSizeBytes: 320L * 1024L * 1024L,
                Entries: new List<CleanupEntry>
                {
                    new(@"C:\Users\Test\AppData\Local\Xunlei\CacheB\b.bin", false, 320L * 1024L * 1024L, DateTime.UtcNow, "ResidualCache")
                }.AsReadOnly())
        };

        var aggregated = BasicScanDashboardViewModel.AggregateFindingsByApp(new[] { first, second });

        aggregated.Should().ContainSingle();
        aggregated[0].TotalSizeBytes.Should().Be(620L * 1024L * 1024L);
        aggregated[0].OriginalBucket.Should().NotBeNull();
        aggregated[0].OriginalBucket!.Entries.Should().HaveCount(2);
    }

    [Fact]
    public void AggregateResidualHotspots_ShouldMergeSamePathAcrossDifferentAppsIntoCommonDeepResidual()
    {
        string sharedPath = @"C:\Users\Test\AppData\Local\Shared\DeepCache";
        var findings = new[]
        {
            new FastScanFinding
            {
                AppId = "Xunlei",
                SizeBytes = 300L * 1024L * 1024L,
                Category = "ResidualCache",
                PrimaryPath = sharedPath,
                SourcePath = sharedPath,
                IsExactSize = true,
                DisplaySize = "300 MB",
                IsHotspot = true,
                IsResidual = true,
                OriginalBucket = new CleanupBucket(
                    BucketId: "xunlei-shared",
                    Category: "ResidualCache",
                    RootPath: sharedPath,
                    AppName: "Xunlei",
                    RiskLevel: RiskLevel.SafeWithPreview,
                    SuggestedAction: CleanupAction.DeleteToRecycleBin,
                    Description: "shared-a",
                    EstimatedSizeBytes: 300L * 1024L * 1024L,
                    Entries: new List<CleanupEntry>
                    {
                        new(Path.Combine(sharedPath, "a.bin"), false, 300L * 1024L * 1024L, DateTime.UtcNow, "ResidualCache")
                    }.AsReadOnly())
            },
            new FastScanFinding
            {
                AppId = "Youku",
                SizeBytes = 280L * 1024L * 1024L,
                Category = "ResidualCache",
                PrimaryPath = sharedPath,
                SourcePath = sharedPath,
                IsExactSize = true,
                DisplaySize = "280 MB",
                IsHotspot = true,
                IsResidual = true,
                OriginalBucket = new CleanupBucket(
                    BucketId: "youku-shared",
                    Category: "ResidualCache",
                    RootPath: sharedPath,
                    AppName: "Youku",
                    RiskLevel: RiskLevel.SafeWithPreview,
                    SuggestedAction: CleanupAction.DeleteToRecycleBin,
                    Description: "shared-b",
                    EstimatedSizeBytes: 280L * 1024L * 1024L,
                    Entries: new List<CleanupEntry>
                    {
                        new(Path.Combine(sharedPath, "b.bin"), false, 280L * 1024L * 1024L, DateTime.UtcNow, "ResidualCache")
                    }.AsReadOnly())
            },
            new FastScanFinding
            {
                AppId = "iQIYI",
                SizeBytes = 260L * 1024L * 1024L,
                Category = "ResidualCache",
                PrimaryPath = sharedPath,
                SourcePath = sharedPath,
                IsExactSize = true,
                DisplaySize = "260 MB",
                IsHotspot = true,
                IsResidual = true,
                OriginalBucket = new CleanupBucket(
                    BucketId: "iqiyi-shared",
                    Category: "ResidualCache",
                    RootPath: sharedPath,
                    AppName: "iQIYI",
                    RiskLevel: RiskLevel.SafeWithPreview,
                    SuggestedAction: CleanupAction.DeleteToRecycleBin,
                    Description: "shared-c",
                    EstimatedSizeBytes: 260L * 1024L * 1024L,
                    Entries: new List<CleanupEntry>
                    {
                        new(Path.Combine(sharedPath, "c.bin"), false, 260L * 1024L * 1024L, DateTime.UtcNow, "ResidualCache")
                    }.AsReadOnly())
            }
        };

        var aggregated = BasicScanDashboardViewModel.AggregateResidualHotspots(findings);

        aggregated.Should().ContainSingle();
        aggregated[0].AppId.Should().Be("常见应用深层残留");
        aggregated[0].PrimaryPath.Should().Be(sharedPath);
        aggregated[0].Category.Should().Contain("Xunlei");
        aggregated[0].Category.Should().Contain("Youku");
        aggregated[0].Category.Should().Contain("iQIYI");
        aggregated[0].OriginalBucket.Should().NotBeNull();
        var bucket = aggregated[0].OriginalBucket!;
        bucket.Entries.Should().HaveCount(3);
        bucket.AppName.Should().Be("常见应用深层残留");
    }

    [Fact]
    public void AggregateResidualHotspots_ShouldKeepDifferentPathsSeparatedForDifferentResidualRoots()
    {
        var findings = new[]
        {
            new FastScanFinding
            {
                AppId = "Xunlei",
                SizeBytes = 300L * 1024L * 1024L,
                Category = "ResidualCache",
                PrimaryPath = @"C:\Users\Test\AppData\Local\Shared\CacheA",
                SourcePath = @"C:\Users\Test\AppData\Local\Shared\CacheA",
                IsExactSize = true,
                DisplaySize = "300 MB",
                IsHotspot = true,
                IsResidual = true
            },
            new FastScanFinding
            {
                AppId = "Youku",
                SizeBytes = 280L * 1024L * 1024L,
                Category = "ResidualCache",
                PrimaryPath = @"C:\Users\Test\AppData\Local\Shared\CacheB",
                SourcePath = @"C:\Users\Test\AppData\Local\Shared\CacheB",
                IsExactSize = true,
                DisplaySize = "280 MB",
                IsHotspot = true,
                IsResidual = true
            }
        };

        var aggregated = BasicScanDashboardViewModel.AggregateResidualHotspots(findings);

        aggregated.Should().HaveCount(2);
        aggregated.Select(item => item.PrimaryPath).Should().BeEquivalentTo(new[]
        {
            @"C:\Users\Test\AppData\Local\Shared\CacheA",
            @"C:\Users\Test\AppData\Local\Shared\CacheB"
        });
    }

    [Fact]
    public void CollapseResidualHotspotsForDisplay_ShouldMergeAllCommonDeepResidualCardsIntoSingleCard()
    {
        var findings = new[]
        {
            new FastScanFinding
            {
                AppId = "常见应用深层残留",
                SizeBytes = 300L * 1024L * 1024L,
                Category = "候选应用: Xunlei / Youku",
                PrimaryPath = @"C:\Users\Test\AppData\Local\Shared\CacheA",
                SourcePath = @"C:\Users\Test\AppData\Local\Shared\CacheA",
                IsExactSize = true,
                DisplaySize = "300 MB",
                IsHotspot = true,
                IsResidual = true,
                IsHeuristicMatch = true,
                OriginalBucket = new CleanupBucket(
                    BucketId: "common-residual-a",
                    Category: "候选应用: Xunlei / Youku",
                    RootPath: @"C:\Users\Test\AppData\Local\Shared\CacheA",
                    AppName: "常见应用深层残留",
                    RiskLevel: RiskLevel.SafeWithPreview,
                    SuggestedAction: CleanupAction.DeleteToRecycleBin,
                    Description: "A",
                    EstimatedSizeBytes: 300L * 1024L * 1024L,
                    Entries: new List<CleanupEntry>
                    {
                        new(@"C:\Users\Test\AppData\Local\Shared\CacheA\a.bin", false, 300L * 1024L * 1024L, DateTime.UtcNow, "ResidualCache")
                    }.AsReadOnly())
            },
            new FastScanFinding
            {
                AppId = "常见应用深层残留",
                SizeBytes = 500L * 1024L * 1024L,
                Category = "候选应用: iQIYI / Youku",
                PrimaryPath = @"C:\Users\Test\AppData\Local\Shared\CacheB",
                SourcePath = @"C:\Users\Test\AppData\Local\Shared\CacheB",
                IsExactSize = true,
                DisplaySize = "500 MB",
                IsHotspot = true,
                IsResidual = true,
                IsHeuristicMatch = true,
                OriginalBucket = new CleanupBucket(
                    BucketId: "common-residual-b",
                    Category: "候选应用: iQIYI / Youku",
                    RootPath: @"C:\Users\Test\AppData\Local\Shared\CacheB",
                    AppName: "常见应用深层残留",
                    RiskLevel: RiskLevel.SafeWithPreview,
                    SuggestedAction: CleanupAction.DeleteToRecycleBin,
                    Description: "B",
                    EstimatedSizeBytes: 500L * 1024L * 1024L,
                    Entries: new List<CleanupEntry>
                    {
                        new(@"C:\Users\Test\AppData\Local\Shared\CacheB\b.bin", false, 500L * 1024L * 1024L, DateTime.UtcNow, "ResidualCache")
                    }.AsReadOnly())
            }
        };

        var collapsed = BasicScanDashboardViewModel.CollapseResidualHotspotsForDisplay(findings);

        collapsed.Should().ContainSingle();
        collapsed[0].AppId.Should().Be("常见应用深层残留");
        collapsed[0].PrimaryPath.Should().Be("已合并 2 个路径");
        collapsed[0].Category.Should().Contain("Xunlei");
        collapsed[0].Category.Should().Contain("Youku");
        collapsed[0].Category.Should().Contain("iQIYI");
        collapsed[0].TotalSizeBytes.Should().Be((300L + 500L) * 1024L * 1024L);
    }

    [Fact]
    public void CollapseResidualHotspotsForDisplay_ShouldKeepPreciseResidualCardsAlongsideMergedCommonCard()
    {
        var findings = new[]
        {
            new FastScanFinding
            {
                AppId = "Quark",
                SizeBytes = 256L * 1024L * 1024L,
                Category = "ResidualCache",
                PrimaryPath = @"C:\Users\Test\AppData\Local\Quark\Cache",
                SourcePath = @"C:\Users\Test\AppData\Local\Quark\Cache",
                IsExactSize = true,
                DisplaySize = "256 MB",
                IsHotspot = true,
                IsResidual = true
            },
            new FastScanFinding
            {
                AppId = "常见应用深层残留",
                SizeBytes = 500L * 1024L * 1024L,
                Category = "候选应用: Xunlei / Youku",
                PrimaryPath = @"C:\Users\Test\AppData\Local\Shared\CacheB",
                SourcePath = @"C:\Users\Test\AppData\Local\Shared\CacheB",
                IsExactSize = true,
                DisplaySize = "500 MB",
                IsHotspot = true,
                IsResidual = true,
                IsHeuristicMatch = true
            },
            new FastScanFinding
            {
                AppId = "常见应用深层残留",
                SizeBytes = 300L * 1024L * 1024L,
                Category = "候选应用: iQIYI",
                PrimaryPath = @"C:\Users\Test\AppData\Local\Shared\CacheC",
                SourcePath = @"C:\Users\Test\AppData\Local\Shared\CacheC",
                IsExactSize = true,
                DisplaySize = "300 MB",
                IsHotspot = true,
                IsResidual = true,
                IsHeuristicMatch = true
            }
        };

        var collapsed = BasicScanDashboardViewModel.CollapseResidualHotspotsForDisplay(findings);

        collapsed.Should().HaveCount(2);
        collapsed.Should().Contain(item => item.AppId == "Quark");
        collapsed.Should().Contain(item => item.AppId == "常见应用深层残留" && item.PrimaryPath == "已合并 2 个路径");
    }

    [Fact]
    public void BuildTargetedFullScanRoots_ShouldIncludeDiscoveredAppRootsFromSearchParents()
    {
        using var sandbox = new TempSandbox("vm-targeted-roots");
        string explicitRoot = sandbox.CreateDirectory("LocalAppData", "Quark");
        string discoveredRoot = sandbox.CreateDirectory("LocalAppData", "ByteDance", "JianyingPro");

        var rules = new[]
        {
            new CleanupRule
            {
                AppName = "Quark",
                AppMatchKeywords = new[] { "Quark" },
                DefaultAction = CleanupAction.DeleteToRecycleBin,
                Targets =
                {
                    new TargetRule { BaseFolder = explicitRoot, Kind = "BrowserCache", RiskLevel = RiskLevel.SafeWithPreview }
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
                            AppTokens = new[] { "quark" }
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
                ResidualFingerprints = new List<ResidualFingerprint>
                {
                    new()
                    {
                        Parent = sandbox.Combine("LocalAppData"),
                        PathKeywords = new List<string> { "jianyingpro" }
                    }
                },
                FastScan = new FastScanHint
                {
                    Category = "MediaCache",
                    MinSizeThreshold = 20L * 1024L * 1024L,
                    SearchHints = new[]
                    {
                        new SearchHint
                        {
                            Parent = sandbox.Combine("LocalAppData"),
                            DirectoryKeywords = new[] { "jianyingpro" }
                        }
                    }
                }
            }
        };

        var roots = BasicScanDashboardViewModel.BuildTargetedFullScanRoots(rules);

        roots.Should().Contain(explicitRoot);
        roots.Should().Contain(discoveredRoot);
        roots.Should().NotContain(path => string.Equals(path, sandbox.Combine("LocalAppData"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildTargetedFullScanRoots_ShouldCollapseNestedRoots_WithoutDroppingDiscoveredRoots()
    {
        using var sandbox = new TempSandbox("vm-targeted-boundary");
        string explicitRoot = sandbox.CreateDirectory("LocalAppData", "Quark");
        string nestedExplicitRoot = sandbox.CreateDirectory("LocalAppData", "Quark", "Cache");
        string parentRoot = sandbox.CreateDirectory("RoamingAppData");

        for (int i = 0; i < 12; i++)
        {
            sandbox.CreateDirectory("RoamingAppData", $"App{i:D2}");
        }

        var rules = new[]
        {
            new CleanupRule
            {
                AppName = "Quark",
                AppMatchKeywords = new[] { "Quark", "App" },
                DefaultAction = CleanupAction.DeleteToRecycleBin,
                Targets =
                {
                    new TargetRule { BaseFolder = explicitRoot, Kind = "BrowserCache", RiskLevel = RiskLevel.SafeWithPreview },
                    new TargetRule { BaseFolder = nestedExplicitRoot, Kind = "BrowserCache", RiskLevel = RiskLevel.SafeWithPreview }
                },
                ResidualFingerprints = new List<ResidualFingerprint>
                {
                    new()
                    {
                        Parent = parentRoot,
                        PathKeywords = new List<string> { "app" }
                    }
                },
                FastScan = new FastScanHint
                {
                    Category = "BrowserCache",
                    SearchHints = new[]
                    {
                        new SearchHint
                        {
                            Parent = parentRoot,
                            DirectoryKeywords = new[] { "app" }
                        }
                    }
                }
            }
        };

        var roots = BasicScanDashboardViewModel.BuildTargetedFullScanRoots(rules);

        roots.Should().Contain(explicitRoot);
        roots.Should().NotContain(nestedExplicitRoot);
        roots.Count(path => path.StartsWith(parentRoot, StringComparison.OrdinalIgnoreCase)).Should().Be(12);
    }

    [Fact]
    public async Task PreviewAndExecuteBucketsAsync_ShouldNotExecuteWhenPreviewCancelled()
    {
        using var sandbox = new TempSandbox("vm-preview-cancel");
        string cacheRoot = sandbox.CreateDirectory("LocalAppData", "Quark", "Cache");
        var bucket = CreateBucket(
            cacheRoot,
            Path.Combine(cacheRoot, "a.bin"),
            Path.Combine(cacheRoot, "b.bin"));

        var dialog = new FakeDialogService();
        var preview = new FakePreviewDialogService
        {
            SelectedEntries = Array.Empty<CleanupEntry>()
        };
        var pipeline = new FakeCleanupPipeline();
        var vm = CreateViewModel(dialog, pipeline, preview);

        var results = await vm.PreviewAndExecuteBucketsAsync(
            new[] { bucket },
            "安全项清理预览",
            "测试安全项");

        results.Should().BeNull();
        preview.WasShowPreviewCalled.Should().BeTrue();
        preview.LastEntries.Should().HaveCount(2);
        pipeline.ExecuteEntriesCalls.Should().BeEmpty();
        pipeline.ExecuteAsyncCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task PreviewAndExecuteBucketsAsync_ShouldExecuteOnlyPreviewSelectedEntries()
    {
        using var sandbox = new TempSandbox("vm-preview-selective");
        string cacheRoot = sandbox.CreateDirectory("LocalAppData", "Quark", "Cache");
        string firstFile = Path.Combine(cacheRoot, "a.bin");
        string secondFile = Path.Combine(cacheRoot, "b.bin");
        string blockedFile = Path.Combine(cacheRoot, "locked.bin");
        var bucket = CreateBucket(cacheRoot, firstFile, secondFile, blockedFile) with
        {
            AllowedRoots = new[] { firstFile, secondFile }
        };

        var dialog = new FakeDialogService();
        var preview = new FakePreviewDialogService();
        var pipeline = new FakeCleanupPipeline();
        preview.SelectedEntries = new[]
        {
            bucket.Entries[1]
        };

        var vm = CreateViewModel(dialog, pipeline, preview);

        var results = await vm.PreviewAndExecuteBucketsAsync(
            new[] { bucket },
            "安全项清理预览",
            "测试安全项");

        results.Should().NotBeNull();
        preview.WasShowPreviewCalled.Should().BeTrue();
        preview.LastEntries.Should().HaveCount(2);
        preview.LastSummary.Should().Contain("已提前阻断 1 个条目");
        pipeline.ExecuteEntriesCalls.Should().ContainSingle();
        pipeline.ExecuteEntriesCalls[0].Entries.Should().ContainSingle(entry =>
            string.Equals(entry.Path, secondFile, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PreviewAndExecuteBucketsAsync_ShouldYieldControlBeforeDeletionCompletes()
    {
        using var sandbox = new TempSandbox("vm-preview-nonblocking");
        string cacheRoot = sandbox.CreateDirectory("LocalAppData", "Quark", "Cache");
        var bucket = CreateBucket(cacheRoot, Path.Combine(cacheRoot, "a.bin"));

        var dialog = new FakeDialogService();
        var preview = new FakePreviewDialogService
        {
            SelectedEntries = bucket.Entries
        };
        var pipeline = new BlockingCleanupPipeline();
        var vm = CreateViewModel(dialog, pipeline, preview);

        var executeTask = vm.PreviewAndExecuteBucketsAsync(
            new[] { bucket },
            "安全项清理预览",
            "测试安全项");

        bool enteredExecution = await pipeline.WaitForExecutionStartAsync(TimeSpan.FromSeconds(3));
        enteredExecution.Should().BeTrue();
        vm.StatusText.Should().Be("正在按预览清单分批执行清理...");
        vm.IsIndeterminate.Should().BeFalse();
        vm.CleanupStageText.Should().Be("分批执行");
        vm.CleanupSummaryText.Should().Contain("已选择 1 个条目");
        vm.CleanupBatchText.Should().Contain("当前批次 1/1");
        vm.CleanupProgressText.Should().Contain("执行进度 0/1");
        vm.CleanupCurrentPathText.Should().Contain("a.bin");
        executeTask.IsCompleted.Should().BeFalse();

        pipeline.ReleaseExecution();

        var results = await executeTask;
        results.Should().NotBeNull();
        vm.IsIndeterminate.Should().BeFalse();
        vm.CleanupStageText.Should().Be("待后台回收");
        vm.CleanupBatchText.Should().Contain("后台正在处理 1 批回收站回收");
        vm.CleanupProgressText.Should().Contain("执行进度 1/1");
        vm.CleanupProgressText.Should().Contain("成功 1");
        vm.CleanupStorageImpactText.Should().Contain("不会立刻增加");
        vm.CleanupCurrentPathText.Should().BeEmpty();
    }

    [Fact]
    public async Task CleanHotspotCommand_ShouldExecuteReverseAttributedBucket()
    {
        using var sandbox = new TempSandbox("vm-clean-hotspot");
        string cacheRoot = sandbox.CreateDirectory("LocalAppData", "Quark", "Cache");
        string filePath = Path.Combine(cacheRoot, "large.bin");
        using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            stream.SetLength(180L * 1024L * 1024L);
        }

        var dialog = new FakeDialogService();
        var pipeline = new FakeCleanupPipeline();
        var preview = new FakePreviewDialogService();
        var vm = CreateViewModel(dialog, pipeline, preview);
        var finding = new FastScanFinding
        {
            AppId = "Quark",
            SizeBytes = 180L * 1024L * 1024L,
            Category = "BrowserCache",
            PrimaryPath = cacheRoot,
            SourcePath = filePath,
            IsExactSize = true,
            DisplaySize = "180 MB",
            IsHotspot = true,
            IsHeuristicMatch = true,
            OriginalBucket = new CleanupBucket(
                BucketId: "reverse-attribution:test",
                Category: "BrowserCache",
                RootPath: cacheRoot,
                AppName: "Quark",
                RiskLevel: RiskLevel.SafeWithPreview,
                SuggestedAction: CleanupAction.DeleteToRecycleBin,
                Description: "Reverse-attributed hotspot candidate",
                EstimatedSizeBytes: 180L * 1024L * 1024L,
                Entries: new List<CleanupEntry>
                {
                    new(filePath, false, 180L * 1024L * 1024L, DateTime.UtcNow, "BrowserCache")
                }.AsReadOnly())
        };

        vm.AppHotspots.Add(finding);
        preview.SelectedEntries = finding.OriginalBucket!.Entries;

        await vm.CleanHotspotCommand.ExecuteAsync(finding);

        dialog.WasConfirmCalled.Should().BeTrue();
        preview.WasShowPreviewCalled.Should().BeTrue();
        pipeline.ExecuteEntriesCalls.Should().ContainSingle();
        pipeline.ExecuteEntriesCalls[0].Entries.Should().ContainSingle(entry => string.Equals(entry.Path, filePath, StringComparison.OrdinalIgnoreCase));
        vm.AppHotspots.Should().BeEmpty();
    }

    [Fact]
    public void CanUseTrustedPreviewEntries_ShouldReturnTrue_ForExactFileScopedBucket()
    {
        using var sandbox = new TempSandbox("vm-trusted-preview");
        string cacheRoot = sandbox.CreateDirectory("LocalAppData", "Youku", "Cache");
        string firstFile = Path.Combine(cacheRoot, "a.bin");
        string secondFile = Path.Combine(cacheRoot, "b.bin");
        var bucket = CreateBucket(cacheRoot, firstFile, secondFile) with
        {
            AllowedRoots = new[] { firstFile, secondFile }
        };

        BasicScanDashboardViewModel.CanUseTrustedPreviewEntries(bucket).Should().BeTrue();
    }

    [Fact]
    public void CanUseTrustedPreviewEntries_ShouldReturnFalse_ForBroadDirectoryBoundary()
    {
        using var sandbox = new TempSandbox("vm-untrusted-preview");
        string cacheRoot = sandbox.CreateDirectory("LocalAppData", "Youku", "Cache");
        string firstFile = Path.Combine(cacheRoot, "a.bin");
        string secondFile = Path.Combine(cacheRoot, "b.bin");
        var bucket = CreateBucket(cacheRoot, firstFile, secondFile);

        BasicScanDashboardViewModel.CanUseTrustedPreviewEntries(bucket).Should().BeFalse();
    }

    [Fact]
    public async Task PreviewAndExecuteBucketsAsync_ShouldExecuteSelectedEntriesInBatches()
    {
        using var sandbox = new TempSandbox("vm-preview-batches");
        string cacheRoot = sandbox.CreateDirectory("LocalAppData", "Quark", "Cache");
        var filePaths = Enumerable.Range(0, BasicScanDashboardViewModel.CleanupExecutionBatchSize + 6)
            .Select(index => Path.Combine(cacheRoot, $"chunk-{index:D3}.bin"))
            .ToArray();
        var bucket = CreateBucket(cacheRoot, filePaths);

        var dialog = new FakeDialogService();
        var preview = new FakePreviewDialogService
        {
            SelectedEntries = bucket.Entries
        };
        var pipeline = new FakeCleanupPipeline();
        var vm = CreateViewModel(dialog, pipeline, preview);

        var results = await vm.PreviewAndExecuteBucketsAsync(
            new[] { bucket },
            "热点清理预览",
            "Quark 热点");

        results.Should().NotBeNull();
        pipeline.ExecuteEntriesCalls.Should().HaveCount(2);
        pipeline.ExecuteEntriesCalls[0].Entries.Should().HaveCount(BasicScanDashboardViewModel.CleanupExecutionBatchSize);
        pipeline.ExecuteEntriesCalls[1].Entries.Should().HaveCount(6);
        vm.CleanupSummaryText.Should().Contain("共 2 批");
        vm.CleanupStageText.Should().Be("待后台回收");
        vm.CleanupStorageImpactText.Should().Contain("不会立刻增加");
        vm.CleanupProgressText.Should().Contain($"执行进度 {filePaths.Length}/{filePaths.Length}");
    }

    [Fact]
    public async Task PreviewAndExecuteBucketsAsync_ShouldUseTrustedBatchSize_ForExactFileScopedHotspot()
    {
        using var sandbox = new TempSandbox("vm-preview-trusted-batches");
        string cacheRoot = sandbox.CreateDirectory("LocalAppData", "Youku", "Cache");
        var filePaths = Enumerable.Range(0, BasicScanDashboardViewModel.CleanupExecutionBatchSize + 6)
            .Select(index => Path.Combine(cacheRoot, $"asset-{index:D3}.bin"))
            .ToArray();
        var bucket = CreateBucket(cacheRoot, filePaths) with
        {
            AllowedRoots = filePaths
        };

        var dialog = new FakeDialogService();
        var preview = new FakePreviewDialogService
        {
            SelectedEntries = bucket.Entries
        };
        var pipeline = new FakeCleanupPipeline();
        var vm = CreateViewModel(dialog, pipeline, preview);

        var results = await vm.PreviewAndExecuteBucketsAsync(
            new[] { bucket },
            "热点清理预览",
            "Youku 热点",
            useTrustedPreviewEntries: true);

        results.Should().NotBeNull();
        pipeline.ExecuteEntriesCalls.Should().ContainSingle();
        pipeline.ExecuteEntriesCalls[0].Entries.Should().HaveCount(filePaths.Length);
        pipeline.ExecuteEntriesCalls[0].AllowTrustedExactFileFastPath.Should().BeTrue();
        vm.CleanupSummaryText.Should().Contain("共 1 批");
    }

    [Fact]
    public async Task PreviewAndExecuteBucketsAsync_ShouldShowPreparationSummaryBeforeConfirmation()
    {
        using var sandbox = new TempSandbox("vm-preview-preparation");
        string cacheRoot = sandbox.CreateDirectory("LocalAppData", "Quark", "Cache");
        string firstFile = Path.Combine(cacheRoot, "a.bin");
        string secondFile = Path.Combine(cacheRoot, "b.bin");
        var bucket = CreateBucket(cacheRoot, firstFile, secondFile);

        var dialog = new FakeDialogService();
        var preview = new FakePreviewDialogService
        {
            SelectedEntries = Array.Empty<CleanupEntry>()
        };
        var pipeline = new FakeCleanupPipeline();
        var vm = CreateViewModel(dialog, pipeline, preview);

        var results = await vm.PreviewAndExecuteBucketsAsync(
            new[] { bucket },
            "热点清理预览",
            "Quark 热点");

        results.Should().BeNull();
        vm.CleanupStageText.Should().Be("已取消");
        preview.LastSummary.Should().Contain("本次共识别出 2 个可执行清理条目");
        pipeline.ExecuteAsyncCalls.Should().BeEmpty();
    }

    private static BasicScanDashboardViewModel CreateViewModel(
        FakeDialogService dialog,
        FakeCleanupPipeline? pipeline = null,
        FakePreviewDialogService? preview = null)
    {
        return new BasicScanDashboardViewModel(
            new LargeFileScanner(),
            new RuleCatalog(Array.Empty<IAppDetector>(), new BucketBuilder()),
            new AppPresenceDetector(),
            pipeline ?? new FakeCleanupPipeline(),
            new AuditLogExporter(),
            dialog,
            preview ?? new FakePreviewDialogService());
    }

    private static CleanupBucket CreateBucket(string rootPath, params string[] filePaths)
    {
        Directory.CreateDirectory(rootPath);
        foreach (var path in filePaths)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                stream.SetLength(1024);
            }
        }

        var entries = filePaths
            .Select((path, index) => new CleanupEntry(
                path,
                false,
                (index + 1) * 64L * 1024L * 1024L,
                DateTime.UtcNow,
                "BrowserCache"))
            .ToList();

        return new CleanupBucket(
            BucketId: $"bucket:{Guid.NewGuid():N}",
            Category: "BrowserCache",
            RootPath: rootPath,
            AppName: "Quark",
            RiskLevel: RiskLevel.SafeAuto,
            SuggestedAction: CleanupAction.DeleteToRecycleBin,
            Description: "Test bucket",
            EstimatedSizeBytes: entries.Sum(entry => entry.SizeBytes),
            Entries: entries.AsReadOnly(),
            AllowedRoots: new[] { rootPath });
    }

    private static CleanupBucket CreateLargeFileBucket(string filePath, long sizeBytes)
    {
        string? rootPath = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(rootPath))
        {
            Directory.CreateDirectory(rootPath);
        }

        if (!File.Exists(filePath))
        {
            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            stream.SetLength(Math.Min(sizeBytes, 1024 * 1024));
        }

        var entry = new CleanupEntry(
            filePath,
            false,
            sizeBytes,
            DateTime.UtcNow,
            "LargeFile");

        return new CleanupBucket(
            BucketId: $"large-file:{filePath}",
            Category: "LargeFile",
            RootPath: rootPath ?? filePath,
            AppName: "LargeFileRadar",
            RiskLevel: RiskLevel.SafeWithPreview,
            SuggestedAction: CleanupAction.DeleteToRecycleBin,
            Description: "大文件雷达结果",
            EstimatedSizeBytes: sizeBytes,
            Entries: new[] { entry },
            AllowedRoots: new[] { filePath });
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

    private class FakeCleanupPipeline : ICleanupPipeline
    {
        public List<(IReadOnlyList<CleanupBucket> Buckets, bool Apply)> ExecuteAsyncCalls { get; } = new();

        public List<(CleanupBucket Bucket, IReadOnlyList<CleanupEntry> Entries, bool Apply, bool AllowTrustedExactFileFastPath)> ExecuteEntriesCalls { get; } = new();

        public HashSet<string> BlockedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public virtual IReadOnlyList<BucketResult> Execute(IReadOnlyList<CleanupBucket> buckets, bool apply)
        {
            ExecuteAsyncCalls.Add((buckets, apply));
            return buckets
                .Select(bucket =>
                {
                    var logs = bucket.Entries
                        .Select(entry => new AuditLogItem(
                            JobId: "test-job",
                            BucketId: bucket.BucketId,
                            TimestampUtc: DateTime.UtcNow,
                            TargetPath: entry.Path,
                            TargetSizeBytes: entry.SizeBytes,
                            Action: bucket.SuggestedAction,
                            Risk: bucket.RiskLevel,
                            AppName: bucket.AppName,
                            Reason: apply
                                ? "Applied"
                                : BlockedPaths.Contains(entry.Path)
                                    ? "Blocked by test"
                                    : "DryRun: Passed Preflight, physical deletion skipped.",
                            Status: apply
                                ? ExecutionStatus.Success
                                : BlockedPaths.Contains(entry.Path)
                                    ? ExecutionStatus.Blocked
                                    : ExecutionStatus.Skipped,
                            ErrorMessage: null))
                        .ToList();

                    return new BucketResult(
                        bucket,
                        apply
                            ? ExecutionStatus.Success
                            : logs.All(log => log.Status == ExecutionStatus.Blocked)
                                ? ExecutionStatus.Blocked
                                : logs.Any(log => log.Status == ExecutionStatus.Blocked)
                                    ? ExecutionStatus.PartialSuccess
                                    : ExecutionStatus.Skipped,
                        apply ? bucket.EstimatedSizeBytes : 0,
                        apply ? bucket.Entries.Count : 0,
                        0,
                        logs.Count(log => log.Status == ExecutionStatus.Blocked),
                        logs);
                })
                .ToArray();
        }

        public virtual Task<IReadOnlyList<BucketResult>> ExecuteAsync(
            IReadOnlyList<CleanupBucket> buckets,
            bool apply,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Execute(buckets, apply));
        }

        public virtual BucketResult ExecuteEntries(
            CleanupBucket parentBucket,
            IEnumerable<CleanupEntry> entriesToApply,
            bool apply,
            bool allowTrustedExactFileFastPath = false)
        {
            var entries = entriesToApply.ToList();
            ExecuteEntriesCalls.Add((parentBucket, entries, apply, allowTrustedExactFileFastPath));
            return new BucketResult(
                parentBucket,
                ExecutionStatus.Success,
                entries.Sum(entry => entry.SizeBytes),
                entries.Count,
                0,
                0,
                entries
                    .Select(entry => new AuditLogItem(
                        JobId: "test-job",
                        BucketId: parentBucket.BucketId,
                        TimestampUtc: DateTime.UtcNow,
                        TargetPath: entry.Path,
                        TargetSizeBytes: entry.SizeBytes,
                        Action: parentBucket.SuggestedAction,
                        Risk: parentBucket.RiskLevel,
                        AppName: parentBucket.AppName,
                        Reason: "Applied",
                        Status: ExecutionStatus.Success,
                        ErrorMessage: null))
                    .ToArray());
        }
    }

    private sealed class BlockingCleanupPipeline : FakeCleanupPipeline
    {
        private readonly TaskCompletionSource<bool> startedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> releaseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override BucketResult ExecuteEntries(
            CleanupBucket parentBucket,
            IEnumerable<CleanupEntry> entriesToApply,
            bool apply,
            bool allowTrustedExactFileFastPath = false)
        {
            startedTcs.TrySetResult(true);
            releaseTcs.Task.GetAwaiter().GetResult();
            return base.ExecuteEntries(parentBucket, entriesToApply, apply, allowTrustedExactFileFastPath);
        }

        public async Task<bool> WaitForExecutionStartAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(startedTcs.Task, Task.Delay(timeout));
            return ReferenceEquals(completed, startedTcs.Task) && startedTcs.Task.Result;
        }

        public void ReleaseExecution()
        {
            releaseTcs.TrySetResult(true);
        }
    }

    private sealed class FakePreviewDialogService : IPreviewDialogService
    {
        public IEnumerable<CleanupEntry> SelectedEntries { get; set; } = Array.Empty<CleanupEntry>();

        public bool WasShowPreviewCalled { get; private set; }

        public string LastTitle { get; private set; } = string.Empty;

        public string LastSummary { get; private set; } = string.Empty;

        public IReadOnlyList<CleanupEntry> LastEntries { get; private set; } = Array.Empty<CleanupEntry>();

        public Task<IEnumerable<CleanupEntry>> ShowPreviewAsync(string title, IEnumerable<CleanupEntry> entries, string? summary = null)
        {
            WasShowPreviewCalled = true;
            LastTitle = title;
            LastSummary = summary ?? string.Empty;
            LastEntries = entries.ToList();

            if (!SelectedEntries.Any())
            {
                return Task.FromResult(Enumerable.Empty<CleanupEntry>());
            }

            return Task.FromResult(SelectedEntries);
        }
    }
}

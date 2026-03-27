using System.Linq;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;
using CDriveMaster.Tests.Helpers;
using FluentAssertions;

namespace CDriveMaster.Tests;

public sealed class AppPresenceDetectorTests
{
    [Fact]
    public async Task EvaluateAppsAsync_WithHeuristicHints_ShouldUseDerivedKeywordsAndLocalTrace()
    {
        using var sandbox = new TempSandbox("app-presence-heuristic");
        string heuristicRoot = sandbox.CreateDirectory("Quark", "quark-cloud-drive", "cache");
        Environment.SetEnvironmentVariable("CDM_PRESENCE_PARENT", sandbox.RootPath);

        try
        {
            var detector = new AppPresenceDetector();
            var rule = new CleanupRule
            {
                AppName = "Quark",
                Description = "Presence rule",
                DefaultAction = CleanupAction.DeleteToRecycleBin,
                FastScan = new FastScanHint
                {
                    IsExperimental = true,
                    Category = "BrowserCache",
                    HeuristicSearchHints = new[]
                    {
                        new HeuristicSearchHint
                        {
                            Parent = "%CDM_PRESENCE_PARENT%",
                            AppTokens = new[] { "quark", "quark-cloud-drive" },
                            CacheTokens = new[] { "cache" }
                        }
                    }
                }
            };

            var results = await detector.EvaluateAppsAsync(new[] { rule });

            results.Should().ContainSingle();
            results[0].AppId.Should().Be("Quark");
            results[0].MatchedEvidences.Should().Contain(evidence => evidence.StartsWith("LocalTrace:"));
            results[0].MatchedEvidences.Should().Contain(evidence => evidence.Contains("quark", System.StringComparison.OrdinalIgnoreCase));
            heuristicRoot.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDM_PRESENCE_PARENT", null);
        }
    }

    [Fact]
    public async Task EvaluateAppsAsync_WithExistingHotPath_ShouldAddFastScanPathEvidence()
    {
        using var sandbox = new TempSandbox("app-presence-hotpath");
        string hotPath = sandbox.CreateDirectory("Chrome", "User Data", "Default", "Cache");

        var detector = new AppPresenceDetector();
        var rule = new CleanupRule
        {
            AppName = "Chrome",
            Description = "HotPath rule",
            DefaultAction = CleanupAction.DeleteToRecycleBin,
            FastScan = new FastScanHint
            {
                HotPaths = new System.Collections.Generic.List<string> { hotPath },
                Category = "BrowserCache"
            }
        };

        var results = await detector.EvaluateAppsAsync(new[] { rule });

        results.Should().ContainSingle();
        results[0].MatchedEvidences.Should().Contain(evidence => evidence.StartsWith("FastScanPath:"));
    }
}

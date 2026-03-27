using System;
using System.Linq;
using CDriveMaster.Core.Interfaces;
using CDriveMaster.Core.Providers;
using CDriveMaster.Core.Services;
using FluentAssertions;

namespace CDriveMaster.Tests;

public sealed class RuleCatalogTests
{
    [Fact]
    public void GetAllProviders_ShouldLoadNewPopularAppRules()
    {
        var catalog = new RuleCatalog(Array.Empty<IAppDetector>(), new BucketBuilder());

        var providers = catalog.GetAllProviders()
            .OfType<GenericRuleProvider>()
            .ToList();

        catalog.FailedRuleErrors.Should().BeEmpty();
        providers.Select(provider => provider.Rule.AppName).Should().Contain(new[]
        {
            "Xunlei",
            "iQIYI",
            "Youku"
        });

        providers
            .Where(provider => provider.Rule.AppName is "Xunlei" or "iQIYI" or "Youku")
            .Should()
            .OnlyContain(provider =>
                provider.Rule.DefaultAction == CDriveMaster.Core.Models.CleanupAction.DeleteToRecycleBin &&
                provider.Rule.Targets.Count >= 4 &&
                provider.Rule.Targets.All(target => target.RiskLevel == CDriveMaster.Core.Models.RiskLevel.SafeWithPreview));
    }
}

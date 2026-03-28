using CDriveMaster.UI.ViewModels;
using FluentAssertions;

namespace CDriveMaster.Tests.ViewModels;

public sealed class MainViewModelTests
{
    [Theory]
    [InlineData("0.6.2-beta", "v0.6.2-beta")]
    [InlineData("v2.1.0-beta.1", "v2.1.0-beta.1")]
    [InlineData(" V3.0.0 ", "V3.0.0")]
    [InlineData("", "v0.0.0-unknown")]
    [InlineData(null, "v0.0.0-unknown")]
    public void NormalizeAppVersion_ShouldReturnExpectedDisplayValue(string? version, string expected)
    {
        MainViewModel.NormalizeAppVersion(version).Should().Be(expected);
    }
}

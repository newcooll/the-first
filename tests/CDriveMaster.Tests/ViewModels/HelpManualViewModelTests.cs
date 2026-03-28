using CDriveMaster.UI.ViewModels;
using FluentAssertions;

namespace CDriveMaster.Tests.ViewModels;

public sealed class HelpManualViewModelTests
{
    [Fact]
    public void Constructor_ShouldLoadEmbeddedManual()
    {
        var viewModel = new HelpManualViewModel();

        viewModel.ManualText.Should().Contain("C盘清理大师 v3.0.x 食用指南");
        viewModel.ManualText.Should().Contain("常规清理");
        viewModel.ManualText.Should().Contain("系统瘦身");
    }
}

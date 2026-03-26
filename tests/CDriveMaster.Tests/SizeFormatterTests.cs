using CDriveMaster.Core.Models;
using CDriveMaster.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace CDriveMaster.Tests;

public sealed class SizeFormatterTests
{
    [Theory]
    [InlineData(512, "512.00 B")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1024 * 1024, "1.00 MB")]
    [InlineData(1024L * 1024L * 1024L, "1.00 GB")]
    public void Format_ShouldReturnHumanReadableText(long bytes, string expected)
    {
        SizeFormatter.Format(bytes).Should().Be(expected);
    }

    [Fact]
    public void LargeFileItem_DisplaySize_ShouldUseFormatter()
    {
        var item = new LargeFileItem(
            FileName: "sample.bin",
            FilePath: @"C:\sample.bin",
            SizeBytes: 5L * 1024L * 1024L,
            LastWriteTime: System.DateTime.Now);

        item.DisplaySize.Should().Be("5.00 MB");
    }
}

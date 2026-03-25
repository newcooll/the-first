using CDriveMaster.Core.Parsers;
using FluentAssertions;
using Xunit;

namespace CDriveMaster.Tests;

public sealed class AnalyzeComponentStoreParserTests
{
    [Fact]
    public void Parse_StandardEnglishOutput_ShouldExtractCorrectly()
    {
        string rawOutput = """
Deployment Image Servicing and Management tool
Actual Size of Component Store : 8.50 GB
Backups and Disabled Features : 1.25 GB
Cache and Temporary Data : 500.00 MB
Number of Reclaimable Packages : 2
Component Store Cleanup Recommended : Yes
""";

        var report = AnalyzeComponentStoreParser.Parse(rawOutput, "op-1");

        report.ActualSizeBytes.Should().Be(9126805504);
        report.BackupsAndDisabledFeaturesBytes.Should().Be(1342177280);
        report.CacheAndTemporaryDataBytes.Should().Be(524288000);
        report.ReclaimablePackageCount.Should().Be(2);
        report.CleanupRecommended.Should().BeTrue();
        report.EstimatedReclaimableBytes.Should().Be(1866465280);
    }

    [Fact]
    public void Parse_NoReclaimableOutput_ShouldHandleZerosAndNo()
    {
        string rawOutput = """
Actual Size of Component Store : 0 Bytes
Backups and Disabled Features : 0 Bytes
Cache and Temporary Data : 0 Bytes
Number of Reclaimable Packages : 0
Component Store Cleanup Recommended : No
""";

        var report = AnalyzeComponentStoreParser.Parse(rawOutput, "op-2");

        report.ActualSizeBytes.Should().Be(0);
        report.BackupsAndDisabledFeaturesBytes.Should().Be(0);
        report.CacheAndTemporaryDataBytes.Should().Be(0);
        report.ReclaimablePackageCount.Should().Be(0);
        report.CleanupRecommended.Should().BeFalse();
        report.EstimatedReclaimableBytes.Should().Be(0);
    }
}

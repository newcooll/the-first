using System;
using System.IO;
using System.Threading.Tasks;
using CDriveMaster.Core.Services;
using CDriveMaster.Tests.Helpers;
using FluentAssertions;

namespace CDriveMaster.Tests.Services;

public sealed class LargeFileScannerTests
{
    [Fact]
    public async Task ScanFullAsync_WithTargetedRoots_ShouldOnlyScanSpecifiedDirectories()
    {
        using var sandbox = new TempSandbox("large-file-targeted");
        string targetedRoot = sandbox.CreateDirectory("TargetedApp", "Cache");
        string ignoredRoot = sandbox.CreateDirectory("IgnoredApp", "Cache");

        string targetedFile = CreateSizedFile(targetedRoot, "targeted.bin", 12L * 1024L * 1024L);
        _ = CreateSizedFile(ignoredRoot, "ignored.bin", 48L * 1024L * 1024L);

        var scanner = new LargeFileScanner();

        var results = await scanner.ScanFullAsync(
            progress: null,
            rootPaths: new[] { targetedRoot },
            topCount: 5);

        results.Should().ContainSingle();
        results[0].FilePath.Should().Be(targetedFile);
    }

    [Fact]
    public async Task ScanFastAsync_WithDepthLimitedRoots_ShouldSkipFilesBeyondConfiguredDepth()
    {
        using var sandbox = new TempSandbox("large-file-fast-depth");
        string root = sandbox.CreateDirectory("Downloads");
        string firstLevel = sandbox.CreateDirectory("Downloads", "Level1");
        string secondLevel = sandbox.CreateDirectory("Downloads", "Level1", "Level2");
        string thirdLevel = sandbox.CreateDirectory("Downloads", "Level1", "Level2", "Level3");

        string rootFile = CreateSizedFile(root, "root.bin", 10L * 1024L * 1024L);
        string secondLevelFile = CreateSizedFile(secondLevel, "second.bin", 20L * 1024L * 1024L);
        _ = CreateSizedFile(thirdLevel, "third.bin", 40L * 1024L * 1024L);

        var scanner = new LargeFileScanner();

        var results = await scanner.ScanFastAsync(
            progress: null,
            roots: new[]
            {
                new LargeFileScanner.FastScanRoot(root, 2)
            },
            topCount: 10);

        results.Should().Contain(item => item.FilePath == rootFile);
        results.Should().Contain(item => item.FilePath == secondLevelFile);
        results.Should().NotContain(item => string.Equals(item.FileName, "third.bin", StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateSizedFile(string directory, string fileName, long length)
    {
        string path = Path.Combine(directory, fileName);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.SetLength(length);
        return path;
    }
}

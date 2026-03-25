using System;
using System.IO;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;
using CDriveMaster.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace CDriveMaster.Tests;

public sealed class OptionalSymlinkTests
{
    [Fact]
    public void BucketBuilder_should_not_traverse_directory_symbolic_link()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new TempSandbox("symlink");
        var root = sandbox.CreateDirectory("WeChat Files", "wxid_123", "FileStorage", "Cache");
        var outside = sandbox.CreateDirectory("OutsideTarget");
        _ = sandbox.CreateFile(Path.Combine("OutsideTarget", "big.bin"), new string('x', 1024));

        var linkPath = Path.Combine(root, "linked-outside");

        try
        {
            _ = Directory.CreateSymbolicLink(linkPath, outside);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }

        var builder = new BucketBuilder();
        var bucket = builder.BuildBucket(
            root,
            "WeChat",
            RiskLevel.SafeAuto,
            CleanupAction.DeleteToRecycleBin,
            "Cache bucket",
            "Cache");

        bucket.Should().NotBeNull();
        bucket!.Entries.Should().OnlyContain(e =>
            !e.Path.Contains("OutsideTarget", StringComparison.OrdinalIgnoreCase));
    }
}

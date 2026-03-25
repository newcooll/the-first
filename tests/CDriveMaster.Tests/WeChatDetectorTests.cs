using System.IO;
using CDriveMaster.Core.Detectors;
using CDriveMaster.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace CDriveMaster.Tests;

public sealed class WeChatDetectorTests
{
    [Fact]
    public void Detect_should_return_not_found_when_no_wechat_markers_exist()
    {
        using var sandbox = new TempSandbox("wechat-detector-empty");
        var docs = sandbox.CreateDirectory("Documents");

        var detector = new WeChatDetector(
            readRegistryPath: () => null,
            getDocumentsPath: () => docs);

        var result = detector.Detect();

        result.Found.Should().BeFalse();
        result.BasePath.Should().BeNull();
    }

    [Fact]
    public void Detect_should_use_documents_fallback_when_registry_returns_mydocument_magic_string()
    {
        using var sandbox = new TempSandbox("wechat-detector-docs");
        var docs = sandbox.CreateDirectory("Documents");
        var wechatRoot = Path.Combine(docs, "WeChat Files");

        Directory.CreateDirectory(Path.Combine(wechatRoot, "wxid_123"));
        File.WriteAllText(Path.Combine(wechatRoot, "wxid_123", "marker.txt"), "ok");

        var detector = new WeChatDetector(
            readRegistryPath: () => "MyDocument:",
            getDocumentsPath: () => docs);

        var result = detector.Detect();

        result.Found.Should().BeTrue();
        result.BasePath.Should().Be(wechatRoot);
        result.Source.Should().Be("ProbeB:RegistryMyDocumentToken");
    }

    [Fact]
    public void Detect_should_return_found_when_registry_path_points_to_valid_applet_root()
    {
        using var sandbox = new TempSandbox("wechat-detector-reg");
        var wechatRoot = sandbox.CreateDirectory("WeChat Files");
        _ = sandbox.CreateDirectory("WeChat Files", "Applet");

        var detector = new WeChatDetector(
            readRegistryPath: () => wechatRoot,
            getDocumentsPath: () => sandbox.Combine("UnusedDocuments"));

        var result = detector.Detect();

        result.Found.Should().BeTrue();
        result.BasePath.Should().Be(wechatRoot);
        result.Source.Should().Be("ProbeA:RegistryFileSavePath");
    }
}

using System;
using System.Reflection;
using CDriveMaster.Core.Detectors;
using CDriveMaster.Core.Interfaces;
using CDriveMaster.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace CDriveMaster.Tests;

public sealed class ChromeDetectorTests
{
    [Fact]
    public void Detect_WhenChromeUserDataExists_ShouldReturnFound()
    {
        using var sandbox = new TempSandbox("chrome-detector-found");
        _ = sandbox.CreateDirectory("Google", "Chrome", "User Data", "Default");

        var detector = new ChromeDetector();

        var result = InvokeDetectWithPath(detector, sandbox.RootPath);

        result.Found.Should().BeTrue();
        result.BasePath.Should().Be(sandbox.Combine("Google", "Chrome", "User Data"));
        result.Source.Should().Be("LocalAppData");
    }

    [Fact]
    public void Detect_WhenNoProfileExists_ShouldReturnNotFound()
    {
        using var sandbox = new TempSandbox("chrome-detector-not-found");
        _ = sandbox.CreateDirectory("Google", "Chrome", "User Data");

        var detector = new ChromeDetector();

        var result = InvokeDetectWithPath(detector, sandbox.RootPath);

        result.Found.Should().BeFalse();
        result.BasePath.Should().BeNull();
        result.Source.Should().Be("LocalAppData");
    }

    private static DetectionResult InvokeDetectWithPath(ChromeDetector detector, string localAppDataPath)
    {
        var method = typeof(ChromeDetector).GetMethod(
            "Detect",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(string) },
            modifiers: null);

        method.Should().NotBeNull("ChromeDetector should expose internal Detect(string) for deterministic tests.");

        var result = method!.Invoke(detector, new object[] { localAppDataPath });
        result.Should().BeOfType<DetectionResult>();

        return (DetectionResult)result!;
    }
}
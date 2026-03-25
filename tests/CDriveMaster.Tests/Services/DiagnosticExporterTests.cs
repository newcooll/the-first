using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using CDriveMaster.Core.Services;
using CDriveMaster.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace CDriveMaster.Tests.Services;

public sealed class DiagnosticExporterTests
{
    [Fact]
    public async Task ExportAsync_WithLogsAndRules_ShouldCreateZipWithExpectedEntries()
    {
        using var outputSandbox = new TempSandbox("diag-output");
        using var appBaseSandbox = new TempSandbox("diag-appbase");

        _ = appBaseSandbox.CreateFile(Path.Combine("Logs", "cleanup.json"), "{\"ok\":true}");
        _ = appBaseSandbox.CreateFile(Path.Combine("Logs", "nested", "audit.json"), "{\"nested\":true}");
        _ = appBaseSandbox.CreateFile(Path.Combine("Rules", "wechat.json"), "{\"app\":\"wechat\"}");

        var fixedNow = new DateTime(2026, 03, 25, 10, 20, 30);
        var exporter = new DiagnosticExporter(
            outputDirectoryProvider: () => outputSandbox.RootPath,
            appBaseDirectoryProvider: () => appBaseSandbox.RootPath,
            nowProvider: () => fixedNow);

        string zipPath = await exporter.ExportAsync();

        zipPath.Should().StartWith(outputSandbox.RootPath);
        File.Exists(zipPath).Should().BeTrue();

        using var archive = ZipFile.OpenRead(zipPath);
        archive.Entries.Select(e => e.FullName).Should().Contain("sysinfo.txt");
        archive.Entries.Select(e => e.FullName).Should().Contain("Logs/cleanup.json");
        archive.Entries.Select(e => e.FullName).Should().Contain("Logs/nested/audit.json");
        archive.Entries.Select(e => e.FullName).Should().Contain("Rules/wechat.json");

        var sysInfoEntry = archive.GetEntry("sysinfo.txt");
        sysInfoEntry.Should().NotBeNull();
        using var reader = new StreamReader(sysInfoEntry!.Open());
        string sysInfo = await reader.ReadToEndAsync();
        sysInfo.Should().Contain("Timestamp: 2026-03-25 10:20:30");
        sysInfo.Should().Contain("OS Version:");
        sysInfo.Should().Contain("Is64BitOperatingSystem:");
        sysInfo.Should().Contain("IsElevated:");
    }

    [Fact]
    public async Task ExportAsync_WithoutLogsAndRules_ShouldStillCreateZipWithSysInfo()
    {
        using var outputSandbox = new TempSandbox("diag-output-empty");
        using var appBaseSandbox = new TempSandbox("diag-appbase-empty");

        var exporter = new DiagnosticExporter(
            outputDirectoryProvider: () => outputSandbox.RootPath,
            appBaseDirectoryProvider: () => appBaseSandbox.RootPath,
            nowProvider: () => DateTime.UtcNow.AddTicks(Guid.NewGuid().GetHashCode()));

        string zipPath = await exporter.ExportAsync();

        File.Exists(zipPath).Should().BeTrue();

        using var archive = ZipFile.OpenRead(zipPath);
        archive.Entries.Select(e => e.FullName).Should().Contain("sysinfo.txt");
        archive.Entries.Select(e => e.FullName).Should().NotContain(e => e.StartsWith("Logs/"));
        archive.Entries.Select(e => e.FullName).Should().NotContain(e => e.StartsWith("Rules/"));
    }
}

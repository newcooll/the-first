using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace CDriveMaster.Core.Services;

public sealed class DiagnosticExporter
{
    private readonly Func<string> outputDirectoryProvider;
    private readonly Func<string> appBaseDirectoryProvider;
    private readonly Func<DateTime> nowProvider;

    public DiagnosticExporter(
        Func<string>? outputDirectoryProvider = null,
        Func<string>? appBaseDirectoryProvider = null,
        Func<DateTime>? nowProvider = null)
    {
        this.outputDirectoryProvider = outputDirectoryProvider
            ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        this.appBaseDirectoryProvider = appBaseDirectoryProvider
            ?? (() => AppContext.BaseDirectory);
        this.nowProvider = nowProvider ?? (() => DateTime.Now);
    }

    public async Task<string> ExportAsync()
    {
        string outputDirectory = outputDirectoryProvider();
        Directory.CreateDirectory(outputDirectory);

        string zipPath = Path.Combine(outputDirectory, $"CDriveMaster_Diag_{nowProvider():yyyyMMdd_HHmmss}.zip");

        string tempPath = Path.GetTempFileName();
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        Directory.CreateDirectory(tempPath);

        try
        {
            string sysInfoPath = Path.Combine(tempPath, "sysinfo.txt");
            string sysInfo = string.Join(Environment.NewLine, new[]
            {
                $"Timestamp: {nowProvider():yyyy-MM-dd HH:mm:ss}",
                $"OS Version: {Environment.OSVersion.VersionString}",
                $"Is64BitOperatingSystem: {Environment.Is64BitOperatingSystem}",
                $"IsElevated: {PlatformProbe.IsElevated}"
            });
            await File.WriteAllTextAsync(sysInfoPath, sysInfo);

            string appBase = appBaseDirectoryProvider();
            string logsPath = Path.Combine(appBase, "Logs");
            if (Directory.Exists(logsPath))
            {
                string tempLogs = Path.Combine(tempPath, "Logs");
                Directory.CreateDirectory(tempLogs);

                var jsonFiles = Directory
                    .EnumerateFiles(logsPath, "*.json", SearchOption.AllDirectories)
                    .ToList();

                foreach (var file in jsonFiles)
                {
                    try
                    {
                        string relativePath = Path.GetRelativePath(logsPath, file);
                        string target = Path.Combine(tempLogs, relativePath);
                        string? targetDir = Path.GetDirectoryName(target);
                        if (!string.IsNullOrWhiteSpace(targetDir))
                        {
                            Directory.CreateDirectory(targetDir);
                        }

                        File.Copy(file, target, overwrite: true);
                    }
                    catch (IOException)
                    {
                        // Ignore a single locked/corrupted file and continue collecting diagnostics.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Ignore a single inaccessible file and continue collecting diagnostics.
                    }
                }
            }

            string rulesPath = Path.Combine(appBase, "Rules");
            if (Directory.Exists(rulesPath))
            {
                string tempRules = Path.Combine(tempPath, "Rules");
                CopyDirectory(rulesPath, tempRules);
            }

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(tempPath, zipPath);
            return zipPath;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempPath))
                {
                    Directory.Delete(tempPath, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            string target = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            string targetSubDir = Path.Combine(targetDir, Path.GetFileName(dir));
            CopyDirectory(dir, targetSubDir);
        }
    }
}

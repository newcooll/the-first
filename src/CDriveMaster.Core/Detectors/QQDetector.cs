using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using CDriveMaster.Core.Interfaces;
using Microsoft.Win32;

namespace CDriveMaster.Core.Detectors;

public sealed class QQDetector : IAppDetector
{
    private readonly Func<string?> _readRegistryPath;
    private readonly Func<string> _getDocumentsPath;

    public QQDetector(
        Func<string?>? readRegistryPath = null,
        Func<string>? getDocumentsPath = null)
    {
        _readRegistryPath = readRegistryPath ?? (() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            return ReadRegistryPath();
        });
        _getDocumentsPath = getDocumentsPath ?? (() =>
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    }

    public string AppName => "QQ";

    public DetectionResult Detect()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var registryPath = _readRegistryPath();
                if (!string.IsNullOrWhiteSpace(registryPath))
                {
                    var probeA = ValidateAndReturn(registryPath, "ProbeA:RegistryQQPath");
                    if (probeA.Found)
                    {
                        return probeA;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (System.Security.SecurityException)
            {
            }
        }

        var fallback = Path.Combine(_getDocumentsPath(), "Tencent Files");
        return ValidateAndReturn(fallback, "ProbeB:DefaultDocumentsFallback");
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistryPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Tencent\QQ", false);
        var directPath = key?.GetValue("FileSavePath") as string;
        if (!string.IsNullOrWhiteSpace(directPath))
        {
            return directPath;
        }

        return key?.GetValue("InstallPath") as string;
    }

    private static DetectionResult ValidateAndReturn(string path, string source)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new DetectionResult(false, null, source, "Path is empty.");
        }

        try
        {
            if (!Directory.Exists(path))
            {
                return new DetectionResult(false, null, source, "Path does not exist.");
            }

            bool hasNumericAccountDirectory = Directory
                .EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Any(x => !string.IsNullOrWhiteSpace(x) && x.All(char.IsDigit));

            bool hasAllUsers = Directory.Exists(Path.Combine(path, "All Users"));

            if (hasNumericAccountDirectory || hasAllUsers)
            {
                return new DetectionResult(true, path, source, "Signature matched.");
            }

            return new DetectionResult(false, null, source, "Signature not found.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new DetectionResult(false, null, source, ex.Message);
        }
        catch (IOException ex)
        {
            return new DetectionResult(false, null, source, ex.Message);
        }
    }
}
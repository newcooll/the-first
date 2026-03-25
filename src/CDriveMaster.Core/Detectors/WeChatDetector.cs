using System;
using System.IO;
using System.Runtime.Versioning;
using CDriveMaster.Core.Interfaces;
using Microsoft.Win32;

namespace CDriveMaster.Core.Detectors;

public class WeChatDetector : IAppDetector
{
    private readonly Func<string?> readRegistryPath;
    private readonly Func<string> getDocumentsPath;

    public WeChatDetector(
        Func<string?>? readRegistryPath = null,
        Func<string>? getDocumentsPath = null)
    {
        this.readRegistryPath = readRegistryPath ?? (() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            return ReadRegistryPath();
        });
        this.getDocumentsPath = getDocumentsPath ?? (() =>
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    }

    public string AppName => "WeChat";

    public DetectionResult Detect()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var value = readRegistryPath();

                if (!string.IsNullOrWhiteSpace(value))
                {
                    if (string.Equals(value, "MyDocument:", StringComparison.OrdinalIgnoreCase))
                    {
                        var documentsPath = getDocumentsPath();
                        var fromMyDocumentToken = Path.Combine(documentsPath, "WeChat Files");
                        var byTokenResult = ValidateAndReturn(fromMyDocumentToken, "ProbeB:RegistryMyDocumentToken");
                        if (byTokenResult.Found)
                        {
                            return byTokenResult;
                        }
                    }
                    else
                    {
                        var probeAResult = ValidateAndReturn(value, "ProbeA:RegistryFileSavePath");
                        if (probeAResult.Found)
                        {
                            return probeAResult;
                        }
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

        var fallbackDocuments = getDocumentsPath();
        var fallbackPath = Path.Combine(fallbackDocuments, "WeChat Files");
        return ValidateAndReturn(fallbackPath, "ProbeC:DefaultDocumentsFallback");
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistryPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Tencent\WeChat", false);
        return key?.GetValue("FileSavePath") as string;
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

            bool hasWxidDirectory = false;
            foreach (var _ in Directory.EnumerateDirectories(path, "wxid_*", SearchOption.TopDirectoryOnly))
            {
                hasWxidDirectory = true;
                break;
            }

            bool hasApplet = Directory.Exists(Path.Combine(path, "Applet"));
            bool hasAllUsers = Directory.Exists(Path.Combine(path, "All Users"));

            if (hasWxidDirectory || hasApplet || hasAllUsers)
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

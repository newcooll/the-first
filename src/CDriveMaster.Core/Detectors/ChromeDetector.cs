using System;
using System.IO;
using CDriveMaster.Core.Interfaces;

namespace CDriveMaster.Core.Detectors;

public sealed class ChromeDetector : IAppDetector
{
    public string AppName => "Chrome";

    public DetectionResult Detect()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Detect(localAppData);
    }

    internal DetectionResult Detect(string localAppDataPath)
    {
        if (string.IsNullOrWhiteSpace(localAppDataPath))
        {
            return new DetectionResult(false, null, "LocalAppData", "LocalAppData path is empty.");
        }

        var userDataRoot = Path.Combine(localAppDataPath, "Google", "Chrome", "User Data");
        if (!Directory.Exists(userDataRoot))
        {
            return new DetectionResult(false, null, "LocalAppData", "Path does not exist.");
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(userDataRoot, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(directory);
                if (string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(name) &&
                     name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)))
                {
                    return new DetectionResult(true, userDataRoot, "LocalAppData", "Signature matched.");
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            return new DetectionResult(false, null, "LocalAppData", ex.Message);
        }
        catch (IOException ex)
        {
            return new DetectionResult(false, null, "LocalAppData", ex.Message);
        }

        return new DetectionResult(false, null, "LocalAppData", "Signature not found.");
    }
}
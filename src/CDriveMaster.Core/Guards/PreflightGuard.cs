using System;
using System.Collections.Generic;
using System.IO;
using CDriveMaster.Core.Services;

namespace CDriveMaster.Core.Guards;

public sealed record PreflightResult(bool Passed, bool IsBlocked, string Reason);

public class PreflightGuard
{
    private readonly HashSet<string> protectedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
    };

    public PreflightResult CheckPath(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return new PreflightResult(false, true, "Target path is empty.");
        }

        try
        {
            var normalizedPath = Path.GetFullPath(targetPath);

            var rootPath = Path.GetPathRoot(normalizedPath);
            if (!string.IsNullOrWhiteSpace(rootPath) &&
                string.Equals(
                    normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return new PreflightResult(false, true, "Target is a drive root. Blocked.");
            }

            foreach (var protectedPath in protectedPrefixes)
            {
                if (string.IsNullOrWhiteSpace(protectedPath))
                {
                    continue;
                }

                if (normalizedPath.StartsWith(protectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return new PreflightResult(false, true, "Target is within a protected system directory.");
                }
            }

            var entry = FsEntry.Resolve(targetPath);
            if (entry is null)
            {
                return new PreflightResult(false, true, "Target not found or inaccessible.");
            }

            if (FsEntry.IsReparsePoint(entry))
            {
                return new PreflightResult(false, true, "Target is a ReparsePoint (Junction/Symlink). Blocked to prevent recursive escape.");
            }

            return new PreflightResult(true, false, "Passed");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new PreflightResult(false, true, ex.Message);
        }
        catch (IOException ex)
        {
            return new PreflightResult(false, true, ex.Message);
        }
    }
}

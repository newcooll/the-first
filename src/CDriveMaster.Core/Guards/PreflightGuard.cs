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

    public PreflightResult CheckPath(string targetPath, IReadOnlyList<string>? allowedRoots = null)
    {
        return CheckPathCore(targetPath, isDirectory: null, allowedRoots, allowExactFileFastPass: false, fastPassReason: null);
    }

    public PreflightResult CheckPathForPreview(
        string targetPath,
        bool isDirectory,
        IReadOnlyList<string>? allowedRoots = null)
    {
        return CheckPathCore(
            targetPath,
            isDirectory,
            allowedRoots,
            allowExactFileFastPass: true,
            fastPassReason: "Preview fast-pass exact file boundary.");
    }

    public PreflightResult CheckPathForExecution(
        string targetPath,
        bool isDirectory,
        IReadOnlyList<string>? allowedRoots = null)
    {
        return CheckPathCore(
            targetPath,
            isDirectory,
            allowedRoots,
            allowExactFileFastPass: true,
            fastPassReason: "Execution fast-pass exact file boundary.");
    }

    private PreflightResult CheckPathCore(
        string targetPath,
        bool? isDirectory,
        IReadOnlyList<string>? allowedRoots,
        bool allowExactFileFastPass,
        string? fastPassReason)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return new PreflightResult(false, true, "Target path is empty.");
        }

        try
        {
            var normalizedPath = Path.GetFullPath(targetPath);
            string trimmedPath = normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var rootPath = Path.GetPathRoot(normalizedPath);
            if (!string.IsNullOrWhiteSpace(rootPath) &&
                string.Equals(
                    trimmedPath,
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

            var normalizedAllowedRoots = NormalizeAllowedRoots(allowedRoots);
            if (normalizedAllowedRoots.Count > 0 && !IsWithinAllowedRoots(normalizedPath, normalizedAllowedRoots))
            {
                return new PreflightResult(false, true, "Target is outside the rule-approved cleanup boundary.");
            }

            if (allowExactFileFastPass
                && isDirectory == false
                && normalizedAllowedRoots.Count > 0
                && normalizedAllowedRoots.Any(root => string.Equals(root, trimmedPath, StringComparison.OrdinalIgnoreCase)))
            {
                return new PreflightResult(true, false, fastPassReason ?? "Exact file boundary fast-pass.");
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

    private static List<string> NormalizeAllowedRoots(IReadOnlyList<string>? allowedRoots)
    {
        if (allowedRoots is null || allowedRoots.Count == 0)
        {
            return new List<string>();
        }

        var normalizedRoots = new List<string>(allowedRoots.Count);
        foreach (var root in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            try
            {
                normalizedRoots.Add(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                continue;
            }
        }

        return normalizedRoots;
    }

    private static bool IsWithinAllowedRoots(string normalizedPath, IReadOnlyList<string> allowedRoots)
    {
        string trimmedPath = normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var allowedRoot in allowedRoots)
        {
            if (string.Equals(trimmedPath, allowedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (trimmedPath.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || trimmedPath.StartsWith(allowedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

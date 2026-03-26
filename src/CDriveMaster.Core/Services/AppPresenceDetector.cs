using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;
using Microsoft.Win32;

namespace CDriveMaster.Core.Services;

public sealed class AppPresenceDetector
{
    public async Task<List<AppEvidenceScore>> EvaluateAppsAsync(IEnumerable<CleanupRule> rules)
    {
        return await Task.Run(() =>
        {
            var results = new List<AppEvidenceScore>();
            var registryEntries = OperatingSystem.IsWindows()
                ? EnumerateUninstallEntries().ToList()
                : new List<InstalledAppInfo>();

            foreach (var rule in rules.Where(r => !string.IsNullOrWhiteSpace(r.AppName)))
            {
                var score = new AppEvidenceScore { AppId = rule.AppName };
                var evidences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var keywords = (rule.AppMatchKeywords ?? Array.Empty<string>())
                    .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                    .ToArray();

                if (keywords.Length > 0)
                {
                    foreach (var app in registryEntries)
                    {
                        foreach (var keyword in keywords)
                        {
                            if (!string.IsNullOrWhiteSpace(app.DisplayName)
                                && app.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                            {
                                var evidence = $"DisplayName:{keyword}";
                                if (evidences.Add(evidence))
                                {
                                    score.TotalScore += 2;
                                    score.MatchedEvidences.Add(evidence);
                                }
                            }

                            bool matchedPublisherOrLocation =
                                (!string.IsNullOrWhiteSpace(app.Publisher)
                                    && app.Publisher.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                                ||
                                (!string.IsNullOrWhiteSpace(app.InstallLocation)
                                    && app.InstallLocation.Contains(keyword, StringComparison.OrdinalIgnoreCase));

                            if (matchedPublisherOrLocation)
                            {
                                var evidence = $"PublisherOrInstallLocation:{keyword}";
                                if (evidences.Add(evidence))
                                {
                                    score.TotalScore += 1;
                                    score.MatchedEvidences.Add(evidence);
                                }
                            }
                        }
                    }
                }

                if (HasStrongLocalTrace(rule, keywords, out string? traceEvidence))
                {
                    var evidence = $"LocalTrace:{traceEvidence}";
                    if (evidences.Add(evidence))
                    {
                        score.TotalScore += 5;
                        score.MatchedEvidences.Add(evidence);
                    }
                }

                Debug.WriteLine($"[AppSurvey] {score.AppId} 得分: {score.TotalScore}, 证据: {string.Join(", ", score.MatchedEvidences)}");
                bool isExperimental = rule.FastScan?.IsExperimental == true;
                int threshold = isExperimental ? 2 : 4;
                if (score.TotalScore >= threshold)
                {
                    results.Add(score);
                }
                else
                {
                    string reason = score.MatchedEvidences.Count == 0
                        ? "无有效证据"
                        : $"证据分不足阈值 {threshold}";
                    Debug.WriteLine($"[AppSurvey] {score.AppId} 被淘汰: {reason}");
                }
            }

            return results;
        });
    }

    private static bool HasStrongLocalTrace(CleanupRule rule, string[] keywords, out string? evidence)
    {
        evidence = null;
        if (keywords.Length == 0)
        {
            return false;
        }

        var localRoots = new[]
        {
            ExpandSafePath("%LOCALAPPDATA%"),
            ExpandSafePath("%APPDATA%"),
            ExpandSafePath("%PROGRAMDATA%")
        }
        .Where(path => !string.IsNullOrWhiteSpace(path) && DirectoryExistsSafe(path!))
        .Select(path => path!)
        .ToList();

        var parentCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (rule.FastScan?.SearchHints is not null)
        {
            foreach (var searchHint in rule.FastScan.SearchHints)
            {
                string? parent = ExpandSafePath(searchHint.Parent);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    parentCandidates.Add(parent);
                }
            }
        }

        if (rule.FastScan?.HotPaths is not null)
        {
            foreach (var hotPath in rule.FastScan.HotPaths)
            {
                string? expandedHotPath = ExpandSafePath(hotPath);
                if (string.IsNullOrWhiteSpace(expandedHotPath))
                {
                    continue;
                }

                string? parent = System.IO.Path.GetDirectoryName(expandedHotPath);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    parentCandidates.Add(parent);
                }
            }
        }

        foreach (var parent in parentCandidates)
        {
            if (!localRoots.Any(root => parent.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!DirectoryExistsSafe(parent))
            {
                continue;
            }

            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((parent, 0));

            while (queue.Count > 0)
            {
                var (currentPath, depth) = queue.Dequeue();
                if (depth > 2)
                {
                    continue;
                }

                IEnumerable<string> subDirs;
                try
                {
                    subDirs = System.IO.Directory.EnumerateDirectories(currentPath, "*", System.IO.SearchOption.TopDirectoryOnly);
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (System.IO.IOException)
                {
                    continue;
                }

                foreach (var subDir in subDirs)
                {
                    string dirName = System.IO.Path.GetFileName(subDir);
                    if (keywords.Any(keyword => dirName.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                    {
                        evidence = subDir;
                        return true;
                    }

                    if (depth < 2)
                    {
                        queue.Enqueue((subDir, depth + 1));
                    }
                }
            }
        }

        return false;
    }

    private static string? ExpandSafePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(rawPath.Replace('/', '\\'));
            return string.IsNullOrWhiteSpace(expanded) ? null : expanded;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool DirectoryExistsSafe(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
        catch (System.IO.IOException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<InstalledAppInfo> EnumerateUninstallEntries()
    {
        var results = new List<InstalledAppInfo>();
        ScanHive(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", RegistryView.Default, results);
        ScanHive(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", RegistryView.Default, results);
        ScanHive(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", RegistryView.Default, results);
        return results;
    }

    [SupportedOSPlatform("windows")]
    private static void ScanHive(
        RegistryHive hive,
        string uninstallPath,
        RegistryView registryView,
        ICollection<InstalledAppInfo> output)
    {
        RegistryKey? baseKey = null;
        RegistryKey? uninstallKey = null;

        try
        {
            baseKey = RegistryKey.OpenBaseKey(hive, registryView);
            uninstallKey = baseKey.OpenSubKey(uninstallPath, writable: false);
            if (uninstallKey is null)
            {
                return;
            }

            foreach (string subKeyName in uninstallKey.GetSubKeyNames())
            {
                RegistryKey? appKey = null;
                try
                {
                    appKey = uninstallKey.OpenSubKey(subKeyName, writable: false);
                    if (appKey is null)
                    {
                        continue;
                    }

                    string? displayName = appKey.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        continue;
                    }

                    output.Add(new InstalledAppInfo(
                        displayName.Trim(),
                        (appKey.GetValue("InstallLocation") as string)?.Trim(),
                        (appKey.GetValue("Publisher") as string)?.Trim(),
                        (appKey.GetValue("DisplayVersion") as string)?.Trim()));
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (System.Security.SecurityException)
                {
                }
                finally
                {
                    appKey?.Dispose();
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (System.Security.SecurityException)
        {
        }
        finally
        {
            uninstallKey?.Dispose();
            baseKey?.Dispose();
        }
    }
}

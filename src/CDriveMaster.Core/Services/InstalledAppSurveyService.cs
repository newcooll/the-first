using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;
using Microsoft.Win32;

namespace CDriveMaster.Core.Services;

public sealed class InstalledAppSurveyService
{
    public async Task<List<InstalledAppInfo>> GetInstalledAppsAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new List<InstalledAppInfo>();
        }

        return await Task.Run(ReadInstalledAppsWindowsOnly);
    }

    [SupportedOSPlatform("windows")]
    private static List<InstalledAppInfo> ReadInstalledAppsWindowsOnly()
    {
        var apps = new Dictionary<string, InstalledAppInfo>(StringComparer.OrdinalIgnoreCase);

        ScanHive(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", RegistryView.Default, apps);
        ScanHive(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", RegistryView.Default, apps);
        ScanHive(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", RegistryView.Default, apps);

        return apps.Values
            .OrderBy(app => app.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [SupportedOSPlatform("windows")]
    private static void ScanHive(
        RegistryHive hive,
        string uninstallPath,
        RegistryView registryView,
        IDictionary<string, InstalledAppInfo> apps)
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
                    if (string.IsNullOrWhiteSpace(displayName) || IsSystemUpdate(displayName))
                    {
                        continue;
                    }

                    var info = new InstalledAppInfo(
                        displayName.Trim(),
                        (appKey.GetValue("InstallLocation") as string)?.Trim(),
                        (appKey.GetValue("Publisher") as string)?.Trim(),
                        (appKey.GetValue("DisplayVersion") as string)?.Trim());

                    if (!apps.ContainsKey(info.DisplayName))
                    {
                        apps[info.DisplayName] = info;
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

    private static bool IsSystemUpdate(string displayName)
    {
        return displayName.Contains("KB", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("Update", StringComparison.OrdinalIgnoreCase);
    }
}

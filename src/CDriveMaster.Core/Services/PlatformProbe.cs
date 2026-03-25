using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace CDriveMaster.Core.Services;

public static class PlatformProbe
{
    [SupportedOSPlatformGuard("windows")]
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static bool IsElevated => IsWindows && IsWindowsElevated();

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}

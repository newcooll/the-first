namespace CDriveMaster.Core.Models;

public record InstalledAppInfo(
    string DisplayName,
    string? InstallLocation,
    string? Publisher,
    string? DisplayVersion);

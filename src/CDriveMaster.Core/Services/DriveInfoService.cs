using System;
using System.IO;

namespace CDriveMaster.Core.Services;

public class DriveInfoService
{
    public DriveSpaceInfo GetDriveSpace(string driveName)
    {
        var drive = new DriveInfo(driveName);

        if (!drive.IsReady)
        {
            throw new InvalidOperationException($"驱动器 {driveName} 不可用。");
        }

        return new DriveSpaceInfo(
            drive.Name,
            drive.TotalSize,
            drive.AvailableFreeSpace,
            drive.TotalSize - drive.AvailableFreeSpace
        );
    }
}

public record DriveSpaceInfo(
    string Name,
    long TotalBytes,
    long FreeBytes,
    long UsedBytes
);
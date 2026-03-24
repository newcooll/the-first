using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Services;

public class RecycleBinService : IRecycleBinService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;
    private const int S_OK = 0;

    public Task<RecycleBinInfo> QueryAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            int result = SHQueryRecycleBin(null, ref info);

            if (result == S_OK)
            {
                return new RecycleBinInfo(info.i64Size, info.i64NumItems);
            }

            return new RecycleBinInfo(0, 0);
        }, cancellationToken);
    }

    public Task<CleanupResult> EmptyAsync(bool showConfirmation, bool showProgressUi, bool playSound, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var preInfo = await QueryAsync(cancellationToken);
            if (preInfo.ItemCount == 0)
            {
                return new CleanupResult(true, 0, 0, null);
            }

            uint flags = 0;
            if (!showConfirmation) flags |= SHERB_NOCONFIRMATION;
            if (!showProgressUi) flags |= SHERB_NOPROGRESSUI;
            if (!playSound) flags |= SHERB_NOSOUND;

            int result = SHEmptyRecycleBin(IntPtr.Zero, null, flags);

            if (result == S_OK)
            {
                return new CleanupResult(true, preInfo.SizeBytes, (int)preInfo.ItemCount, null);
            }

            return new CleanupResult(false, 0, 0, $"Shell API 返回错误码: {result}");
        }, cancellationToken);
    }
}

using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Executors;

public sealed class WindowsCleanupDeleteBackend : ICleanupDeleteBackend
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    public void Delete(CleanupEntry entry, CleanupAction action)
    {
        if (entry.IsDirectory)
        {
            if (!Directory.Exists(entry.Path))
            {
                return;
            }

            if (action == CleanupAction.DeleteToRecycleBin && OperatingSystem.IsWindows())
            {
                DeleteToRecycleBin(entry.Path);
                return;
            }

            Directory.Delete(entry.Path, recursive: true);
            return;
        }

        if (!File.Exists(entry.Path))
        {
            return;
        }

        if (action == CleanupAction.DeleteToRecycleBin && OperatingSystem.IsWindows())
        {
            DeleteToRecycleBin(entry.Path);
            return;
        }

        File.Delete(entry.Path);
    }

    public IReadOnlyList<CleanupDeleteResult> DeleteMany(IReadOnlyList<CleanupEntry> entries, CleanupAction action)
    {
        if (entries.Count == 0)
        {
            return Array.Empty<CleanupDeleteResult>();
        }

        if (action != CleanupAction.DeleteToRecycleBin
            || !OperatingSystem.IsWindows()
            || entries.Any(entry => entry.IsDirectory))
        {
            return DeleteIndividually(entries, action);
        }

        var normalizedEntries = entries
            .Select(entry => new
            {
                Entry = entry,
                NormalizedPath = Path.GetFullPath(entry.Path)
            })
            .GroupBy(item => item.NormalizedPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (normalizedEntries.Count == 0)
        {
            return Array.Empty<CleanupDeleteResult>();
        }

        string multiString = string.Join('\0', normalizedEntries.Select(item => item.NormalizedPath)) + '\0' + '\0';
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = multiString,
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT
        };

        int result = SHFileOperation(ref operation);
        string? failureMessage = null;
        if (result != 0)
        {
            failureMessage = new Win32Exception(result).Message;
        }
        else if (operation.fAnyOperationsAborted)
        {
            failureMessage = "Recycle bin delete aborted.";
        }

        var deleteResults = new List<CleanupDeleteResult>(normalizedEntries.Count);
        var remainingEntries = new List<CleanupEntry>();
        foreach (var item in normalizedEntries)
        {
            bool stillExists = File.Exists(item.NormalizedPath) || Directory.Exists(item.NormalizedPath);
            if (!stillExists)
            {
                deleteResults.Add(new CleanupDeleteResult(item.Entry.Path, ExecutionStatus.Success));
                continue;
            }

            remainingEntries.Add(item.Entry);
        }

        if (remainingEntries.Count == 0)
        {
            return deleteResults;
        }

        foreach (var retryResult in DeleteIndividually(remainingEntries, action))
        {
            if (retryResult.Status == ExecutionStatus.Success || string.IsNullOrWhiteSpace(failureMessage))
            {
                deleteResults.Add(retryResult);
                continue;
            }

            deleteResults.Add(retryResult with
            {
                ErrorMessage = string.IsNullOrWhiteSpace(retryResult.ErrorMessage)
                    ? failureMessage
                    : $"{failureMessage} | {retryResult.ErrorMessage}"
            });
        }

        return deleteResults;
    }

    private static void DeleteToRecycleBin(string path)
    {
        string normalizedPath = Path.GetFullPath(path);
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = normalizedPath + '\0' + '\0',
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT
        };

        int result = SHFileOperation(ref operation);
        if (result != 0)
        {
            throw new IOException(
                $"Recycle bin delete failed for '{normalizedPath}': {new Win32Exception(result).Message}",
                new Win32Exception(result));
        }

        if (operation.fAnyOperationsAborted)
        {
            throw new IOException($"Recycle bin delete aborted for '{normalizedPath}'.");
        }
    }

    private static IReadOnlyList<CleanupDeleteResult> DeleteIndividually(
        IReadOnlyList<CleanupEntry> entries,
        CleanupAction action)
    {
        var results = new List<CleanupDeleteResult>(entries.Count);
        foreach (var entry in entries)
        {
            try
            {
                var backend = new WindowsCleanupDeleteBackend();
                backend.Delete(entry, action);
                results.Add(new CleanupDeleteResult(entry.Path, ExecutionStatus.Success));
            }
            catch (IOException ex)
            {
                results.Add(new CleanupDeleteResult(entry.Path, ExecutionStatus.Skipped, ex.Message));
            }
            catch (UnauthorizedAccessException ex)
            {
                results.Add(new CleanupDeleteResult(entry.Path, ExecutionStatus.Skipped, ex.Message));
            }
            catch (Exception ex)
            {
                results.Add(new CleanupDeleteResult(entry.Path, ExecutionStatus.Failed, ex.Message));
            }
        }

        return results;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}

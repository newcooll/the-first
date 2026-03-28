using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Executors;

public sealed class WindowsCleanupDeleteBackend : ICleanupDeleteBackend
{
    internal const string PendingDeleteDetailMessage = "Moved to pending-delete queue; recycle bin cleanup continues in background.";
    internal const string RecycleBinDetailMessage = "Moved to recycle bin.";
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private static readonly string PendingDeleteRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CDriveMaster",
        "PendingDelete");

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

        if (action != CleanupAction.DeleteToRecycleBin || !OperatingSystem.IsWindows())
        {
            return DeleteIndividually(entries, action);
        }

        var stagedResults = TryStageEntriesForBackgroundDelete(entries);
        if (stagedResults is not null)
        {
            return stagedResults;
        }

        if (entries.Any(entry => entry.IsDirectory))
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
                deleteResults.Add(new CleanupDeleteResult(
                    item.Entry.Path,
                    ExecutionStatus.Success,
                    DetailMessage: RecycleBinDetailMessage));
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

    private static IReadOnlyList<CleanupDeleteResult>? TryStageEntriesForBackgroundDelete(IReadOnlyList<CleanupEntry> entries)
    {
        if (entries.Count == 0)
        {
            return Array.Empty<CleanupDeleteResult>();
        }

        string pendingRoot = Path.GetFullPath(PendingDeleteRoot);
        string pendingVolume = NormalizeRoot(Path.GetPathRoot(pendingRoot));
        if (string.IsNullOrWhiteSpace(pendingVolume))
        {
            return null;
        }

        var stagedItems = new List<StagedDeleteItem>(entries.Count);
        foreach (var entry in entries)
        {
            string sourcePath = Path.GetFullPath(entry.Path);
            string sourceVolume = NormalizeRoot(Path.GetPathRoot(sourcePath));
            if (!string.Equals(sourceVolume, pendingVolume, StringComparison.OrdinalIgnoreCase))
            {
                RollbackStagedItems(stagedItems);
                return null;
            }

            try
            {
                string stageDirectory = Path.Combine(
                    pendingRoot,
                    DateTime.UtcNow.ToString("yyyyMMdd"),
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stageDirectory);

                string destinationPath = Path.Combine(stageDirectory, Path.GetFileName(sourcePath));
                if (entry.IsDirectory)
                {
                    Directory.Move(sourcePath, destinationPath);
                }
                else
                {
                    File.Move(sourcePath, destinationPath);
                }

                stagedItems.Add(new StagedDeleteItem(entry, destinationPath));
            }
            catch
            {
                RollbackStagedItems(stagedItems);
                return null;
            }
        }

        _ = Task.Run(() => DeleteStagedItems(stagedItems));

        return stagedItems
            .Select(item => new CleanupDeleteResult(
                item.OriginalEntry.Path,
                ExecutionStatus.Success,
                DetailMessage: PendingDeleteDetailMessage))
            .ToArray();
    }

    private static void DeleteStagedItems(IReadOnlyList<StagedDeleteItem> stagedItems)
    {
        if (stagedItems.Count == 0)
        {
            return;
        }

        var stagedEntries = stagedItems
            .Select(item => item.OriginalEntry with { Path = item.StagedPath })
            .ToArray();

        try
        {
            DeleteIndividually(stagedEntries, CleanupAction.DeleteToRecycleBin);
        }
        catch
        {
        }
    }

    private static void RollbackStagedItems(IReadOnlyList<StagedDeleteItem> stagedItems)
    {
        for (int index = stagedItems.Count - 1; index >= 0; index--)
        {
            var item = stagedItems[index];
            try
            {
                if (item.OriginalEntry.IsDirectory)
                {
                    if (Directory.Exists(item.StagedPath))
                    {
                        Directory.Move(item.StagedPath, item.OriginalEntry.Path);
                    }
                }
                else if (File.Exists(item.StagedPath))
                {
                    string? originalDirectory = Path.GetDirectoryName(item.OriginalEntry.Path);
                    if (!string.IsNullOrWhiteSpace(originalDirectory))
                    {
                        Directory.CreateDirectory(originalDirectory);
                    }

                    File.Move(item.StagedPath, item.OriginalEntry.Path);
                }
            }
            catch
            {
            }
        }
    }

    private static string NormalizeRoot(string? root)
    {
        return string.IsNullOrWhiteSpace(root)
            ? string.Empty
            : root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
                results.Add(new CleanupDeleteResult(
                    entry.Path,
                    ExecutionStatus.Success,
                    DetailMessage: action == CleanupAction.DeleteToRecycleBin && OperatingSystem.IsWindows()
                        ? RecycleBinDetailMessage
                        : null));
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

    private sealed record StagedDeleteItem(CleanupEntry OriginalEntry, string StagedPath);
}

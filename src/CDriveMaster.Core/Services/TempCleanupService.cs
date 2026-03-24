using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Services;

public class TempCleanupService : ITempCleanupService
{
    private static readonly EnumerationOptions SafeEnumOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false
    };

    private static readonly TimeSpan SafeAgeThreshold = TimeSpan.FromHours(1);
    private const int MaxIssueSamples = 50;

    public async Task<TempCleanupResult> CleanupAsync(CancellationToken cancellationToken, IProgress<CleanupProgress>? progress = null)
    {
        var tempRoot = Path.GetTempPath();
        var tempRootFullPath = Path.GetFullPath(tempRoot);

        var state = new CleanupState();

        await Task.Run(() =>
        {
            if (Directory.Exists(tempRootFullPath))
            {
                CleanDirectoryBottomUp(new DirectoryInfo(tempRootFullPath), tempRootFullPath, state, cancellationToken, progress);
            }
        }, cancellationToken);

        return new TempCleanupResult(
            state.ReleasedBytes,
            state.DeletedFileCount,
            state.DeletedDirectoryCount,
            state.SkippedLockedFileCount,
            state.SkippedAccessDeniedCount,
            state.SkippedReparsePointCount,
            state.SkippedTooNewCount,
            state.FailedDirectoryDeleteCount,
            state.SampleIssues.AsReadOnly()
        );
    }

    private void CleanDirectoryBottomUp(
        DirectoryInfo dir,
        string rootFullPath,
        CleanupState state,
        CancellationToken token,
        IProgress<CleanupProgress>? progress)
    {
        token.ThrowIfCancellationRequested();

        if (!dir.FullName.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase))
        {
            RecordIssue(state, dir.FullName, "触发越界逃逸保护");
            return;
        }

        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            state.SkippedReparsePointCount++;
            RecordIssue(state, dir.FullName, "目录为重解析点 (ReparsePoint)");
            return;
        }

        foreach (var file in dir.EnumerateFiles("*", SafeEnumOptions))
        {
            token.ThrowIfCancellationRequested();

            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                state.SkippedReparsePointCount++;
                continue;
            }

            if ((file.Attributes & FileAttributes.Offline) != 0)
            {
                RecordIssue(state, file.FullName, "离线文件 (Offline)");
                continue;
            }

            if (DateTime.Now - file.LastWriteTime < SafeAgeThreshold)
            {
                state.SkippedTooNewCount++;
                continue;
            }

            try
            {
                long size = file.Length;

                if (file.IsReadOnly)
                {
                    file.IsReadOnly = false;
                }

                file.Delete();

                state.ReleasedBytes += size;
                state.DeletedFileCount++;

                if (state.DeletedFileCount % 100 == 0)
                {
                    progress?.Report(new CleanupProgress($"正在清理: {file.Name}"));
                }
            }
            catch (IOException)
            {
                state.SkippedLockedFileCount++;
            }
            catch (UnauthorizedAccessException)
            {
                state.SkippedAccessDeniedCount++;
                RecordIssue(state, file.FullName, "拒绝访问 (UnauthorizedAccessException)");
            }
            catch (Exception ex)
            {
                RecordIssue(state, file.FullName, $"删除失败: {ex.Message}");
            }
        }

        foreach (var subDir in dir.EnumerateDirectories("*", SafeEnumOptions))
        {
            CleanDirectoryBottomUp(subDir, rootFullPath, state, token, progress);
        }

        if (string.Equals(dir.FullName, rootFullPath, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            dir.Delete(false);
            state.DeletedDirectoryCount++;
        }
        catch (IOException)
        {
            state.FailedDirectoryDeleteCount++;
        }
        catch (UnauthorizedAccessException)
        {
            state.FailedDirectoryDeleteCount++;
        }
    }

    private void RecordIssue(CleanupState state, string path, string reason)
    {
        if (state.SampleIssues.Count < MaxIssueSamples)
        {
            state.SampleIssues.Add(new CleanupIssue(path, reason));
        }
    }

    private class CleanupState
    {
        public long ReleasedBytes;
        public int DeletedFileCount;
        public int DeletedDirectoryCount;
        public int SkippedLockedFileCount;
        public int SkippedAccessDeniedCount;
        public int SkippedReparsePointCount;
        public int SkippedTooNewCount;
        public int FailedDirectoryDeleteCount;
        public List<CleanupIssue> SampleIssues = new();
    }
}

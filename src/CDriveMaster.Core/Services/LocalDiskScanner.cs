using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Services;

public class LocalDiskScanner : IDiskScanService
{
    private const int TopN = 20;
    private static readonly EnumerationOptions SafeEnumOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false
    };

    public async Task<ScanSnapshot> ScanAsync(IReadOnlyList<ScanTarget> targets, CancellationToken cancellationToken, IProgress<ScanProgress>? progress = null)
    {
        var startedAt = DateTimeOffset.Now;

        long globalTotalBytes = 0;
        int globalTotalFiles = 0;
        int globalTotalDirs = 0;

        var categoryStats = new List<CategoryStat>();
        var topFiles = new PriorityQueue<FileStat, long>();
        var topDirs = new PriorityQueue<DirectoryStat, long>();
        var issues = new List<ScanIssue>();

        await Task.Run(() =>
        {
            int targetIndex = 0;
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int phasePercent = (int)((double)targetIndex / targets.Count * 100);
                progress?.Report(new ScanProgress($"正在扫描阶段 [{target.Category}]: {target.Path}", phasePercent));

                long catBytes = 0;
                int catFiles = 0;
                int catDirs = 0;
                int filesSinceLastReport = 0;

                if (Directory.Exists(target.Path))
                {
                    ScanDirectory(target.Path, ref catBytes, ref catFiles, ref catDirs, ref filesSinceLastReport,
                                  topFiles, topDirs, issues, cancellationToken, progress);
                }
                else
                {
                    issues.Add(new ScanIssue(target.Path, "目标路径不存在"));
                }

                categoryStats.Add(new CategoryStat(target.Category, catBytes, catFiles, catDirs));

                globalTotalBytes += catBytes;
                globalTotalFiles += catFiles;
                globalTotalDirs += catDirs;
                targetIndex++;
            }
        }, cancellationToken);

        return new ScanSnapshot(
            Targets: targets,
            StartedAt: startedAt,
            FinishedAt: DateTimeOffset.Now,
            TotalBytes: globalTotalBytes,
            TotalFiles: globalTotalFiles,
            TotalDirectories: globalTotalDirs,
            CategoryStats: categoryStats.AsReadOnly(),
            LargestDirectories: ExtractTopItems(topDirs),
            LargestFiles: ExtractTopItems(topFiles),
            Issues: issues.AsReadOnly()
        );
    }

    private long ScanDirectory(
        string path, ref long catBytes, ref int catFiles, ref int catDirs, ref int filesSinceLastReport,
        PriorityQueue<FileStat, long> topFiles, PriorityQueue<DirectoryStat, long> topDirs, List<ScanIssue> issues,
        CancellationToken token, IProgress<ScanProgress>? progress)
    {
        token.ThrowIfCancellationRequested();
        long currentDirBytes = 0;
        int currentDirFiles = 0;
        int currentDirSubDirs = 0;

        try
        {
            var dirInfo = new DirectoryInfo(path);

            foreach (var file in dirInfo.EnumerateFiles("*", SafeEnumOptions))
            {
                token.ThrowIfCancellationRequested();
                currentDirBytes += file.Length;
                currentDirFiles++;

                catBytes += file.Length;
                catFiles++;
                filesSinceLastReport++;

                EnqueueTop(topFiles, new FileStat(file.FullName, file.Name, file.Length, file.LastWriteTime), file.Length, TopN);

                if (filesSinceLastReport >= 1000)
                {
                    progress?.Report(new ScanProgress($"正在分析: {file.DirectoryName}", 0));
                    filesSinceLastReport = 0;
                }
            }

            foreach (var subDir in dirInfo.EnumerateDirectories("*", SafeEnumOptions))
            {
                catDirs++;
                currentDirSubDirs++;
                long subDirSize = ScanDirectory(subDir.FullName, ref catBytes, ref catFiles, ref catDirs, ref filesSinceLastReport,
                                                topFiles, topDirs, issues, token, progress);
                currentDirBytes += subDirSize;
            }

            EnqueueTop(topDirs, new DirectoryStat(path, currentDirBytes, currentDirFiles, currentDirSubDirs), currentDirBytes, TopN);
        }
        catch (UnauthorizedAccessException)
        {
            issues.Add(new ScanIssue(path, "无权访问 (UnauthorizedAccessException)"));
        }
        catch (Exception ex) when (ex is PathTooLongException || ex is IOException)
        {
            issues.Add(new ScanIssue(path, $"访问异常: {ex.Message}"));
        }

        return currentDirBytes;
    }

    private static void EnqueueTop<T>(PriorityQueue<T, long> queue, T item, long size, int maxCount)
    {
        queue.Enqueue(item, size);
        if (queue.Count > maxCount) queue.Dequeue();
    }

    private static IReadOnlyList<T> ExtractTopItems<T>(PriorityQueue<T, long> queue)
    {
        var list = new List<T>(queue.Count);
        while (queue.TryDequeue(out var item, out _)) list.Add(item);
        list.Reverse();
        return list.AsReadOnly();
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Services;

public sealed class LargeFileScanner
{
    public sealed record ScanProgress(
        string CurrentPath,
        bool IsIndeterminate,
        double Percentage,
        int ScannedDirs,
        int ScannedFiles,
        int SkippedCount,
        TimeSpan Elapsed
    );

    public async Task<List<LargeFileItem>> ScanFullAsync(
        IProgress<ScanProgress>? progress,
        int topCount = 20,
        CancellationToken ct = default)
    {
        if (topCount <= 0)
        {
            return new List<LargeFileItem>();
        }

        return await Task.Run(() =>
        {
            var queue = new PriorityQueue<LargeFileItem, long>();
            var stopwatch = Stopwatch.StartNew();
            long lastReportMs = -100;
            int scannedDirs = 0;
            int scannedFiles = 0;
            int skippedCount = 0;

            string systemPath = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string rootPath = Path.GetPathRoot(systemPath) ?? "C:\\";
            ScanDirectory(
                rootPath,
                queue,
                topCount,
                progress,
                ct,
                stopwatch,
                ref lastReportMs,
                true,
                0,
                ref scannedDirs,
                ref scannedFiles,
                ref skippedCount);

            ReportProgress(
                progress,
                rootPath,
                true,
                0,
                scannedDirs,
                scannedFiles,
                skippedCount,
                stopwatch,
                ref lastReportMs,
                force: true);

            return ExtractTopItems(queue);
        }, ct);
    }

    public async Task<List<LargeFileItem>> ScanFastAsync(
        IProgress<ScanProgress>? progress,
        int topCount = 20,
        CancellationToken ct = default)
    {
        if (topCount <= 0)
        {
            return new List<LargeFileItem>();
        }

        return await Task.Run(() =>
        {
            var queue = new PriorityQueue<LargeFileItem, long>();
            var stopwatch = Stopwatch.StartNew();
            long lastReportMs = -100;
            int scannedDirs = 0;
            int scannedFiles = 0;
            int skippedCount = 0;

            var candidatePaths = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.ExpandEnvironmentVariables("%TEMP%"),
                Environment.ExpandEnvironmentVariables("%SystemRoot%\\Temp")
            }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

            int totalPaths = candidatePaths.Count;
            int index = 0;

            foreach (var path in candidatePaths)
            {
                ct.ThrowIfCancellationRequested();
                double percentage = totalPaths == 0 ? 100 : ((double)index / totalPaths) * 100d;
                ScanDirectory(
                    path,
                    queue,
                    topCount,
                    progress,
                    ct,
                    stopwatch,
                    ref lastReportMs,
                    false,
                    percentage,
                    ref scannedDirs,
                    ref scannedFiles,
                    ref skippedCount);

                index++;
            }

            ReportProgress(
                progress,
                "快速扫描完成",
                false,
                100,
                scannedDirs,
                scannedFiles,
                skippedCount,
                stopwatch,
                ref lastReportMs,
                force: true);

            return ExtractTopItems(queue);
        }, ct);
    }

    public async Task<List<LargeFileItem>> ScanTopFilesAsync(
        string rootPath,
        int topCount = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath) || topCount <= 0)
        {
            return new List<LargeFileItem>();
        }

        return await Task.Run(() =>
        {
            var queue = new PriorityQueue<LargeFileItem, long>();
            var stopwatch = Stopwatch.StartNew();
            long lastReportMs = -100;
            int scannedDirs = 0;
            int scannedFiles = 0;
            int skippedCount = 0;

            ScanDirectory(
                rootPath,
                queue,
                topCount,
                progress: null,
                cancellationToken,
                stopwatch,
                ref lastReportMs,
                true,
                0,
                ref scannedDirs,
                ref scannedFiles,
                ref skippedCount);

            return ExtractTopItems(queue);
        }, cancellationToken);
    }

    private void ScanDirectory(
        string path,
        PriorityQueue<LargeFileItem, long> topFiles,
        int topCount,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        long lastReportMs = -100;
        int scannedDirs = 0;
        int scannedFiles = 0;
        int skippedCount = 0;
        ScanDirectory(
            path,
            topFiles,
            topCount,
            progress,
            ct,
            stopwatch,
            ref lastReportMs,
            true,
            0,
            ref scannedDirs,
            ref scannedFiles,
            ref skippedCount);
    }

    private void ScanDirectory(
        string path,
        PriorityQueue<LargeFileItem, long> topFiles,
        int topCount,
        IProgress<ScanProgress>? progress,
        CancellationToken ct,
        Stopwatch stopwatch,
        ref long lastReportMs,
        bool isIndeterminate,
        double percentage,
        ref int scannedDirs,
        ref int scannedFiles,
        ref int skippedCount)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        var pending = new Stack<string>();
        pending.Push(path);

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false
        };

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            string currentDir = pending.Pop();
            scannedDirs++;

            ReportProgress(
                progress,
                currentDir,
                isIndeterminate,
                percentage,
                scannedDirs,
                scannedFiles,
                skippedCount,
                stopwatch,
                ref lastReportMs,
                force: false);

            try
            {
                foreach (var filePath in Directory.EnumerateFiles(currentDir, "*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    scannedFiles++;

                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        if (!fileInfo.Exists)
                        {
                            continue;
                        }

                        var item = new LargeFileItem(
                            FileName: fileInfo.Name,
                            FilePath: fileInfo.FullName,
                            SizeBytes: fileInfo.Length,
                            LastWriteTime: fileInfo.LastWriteTime);

                        topFiles.Enqueue(item, item.SizeBytes);
                        if (topFiles.Count > topCount)
                        {
                            topFiles.Dequeue();
                        }
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                    {
                        skippedCount++;
                    }
                }

                foreach (var subDir in Directory.EnumerateDirectories(currentDir, "*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    pending.Push(subDir);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                skippedCount++;
            }
        }
    }

    private static void ReportProgress(
        IProgress<ScanProgress>? progress,
        string currentPath,
        bool isIndeterminate,
        double percentage,
        int scannedDirs,
        int scannedFiles,
        int skippedCount,
        Stopwatch stopwatch,
        ref long lastReportMs,
        bool force)
    {
        if (progress is null)
        {
            return;
        }

        long now = stopwatch.ElapsedMilliseconds;
        if (!force && now - lastReportMs < 100)
        {
            return;
        }

        lastReportMs = now;
        progress.Report(new ScanProgress(
            CurrentPath: $"正在扫描: {currentPath}...",
            IsIndeterminate: isIndeterminate,
            Percentage: Math.Clamp(percentage, 0, 100),
            ScannedDirs: scannedDirs,
            ScannedFiles: scannedFiles,
            SkippedCount: skippedCount,
            Elapsed: stopwatch.Elapsed));
    }

    private static List<LargeFileItem> ExtractTopItems(PriorityQueue<LargeFileItem, long> queue)
    {
        var result = new List<LargeFileItem>(queue.Count);
        while (queue.Count > 0)
        {
            result.Add(queue.Dequeue());
        }

        result.Reverse();
        return result;
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Services;

public sealed class DeepScanService
{
    public async Task<DeepScanResult> ScanAsync(string rootPath, string title, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return new DeepScanResult(
                Title: string.IsNullOrWhiteSpace(title) ? "深度扫描" : title,
                RootPath: rootPath,
                ExactSizeBytes: 0,
                FileCount: 0,
                DirectoryCount: 0,
                InaccessibleCount: 0,
                HasPermissionIssue: false,
                Items: Array.Empty<DeepScanItem>());
        }

        var topItems = new PriorityQueue<DeepScanItem, long>();
        var pendingDirs = new Stack<string>();
        pendingDirs.Push(rootPath);

        long exactSizeBytes = 0;
        int fileCount = 0;
        int directoryCount = 0;
        int inaccessibleCount = 0;
        bool hasPermissionIssue = false;

        Stopwatch sw = Stopwatch.StartNew();

        while (pendingDirs.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            string currentDir = pendingDirs.Pop();
            directoryCount++;

            if (sw.ElapsedMilliseconds > 50)
            {
                await Task.Delay(1, ct);
                sw.Restart();
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(currentDir, "*", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                inaccessibleCount++;
                hasPermissionIssue = true;
                continue;
            }
            catch (IOException)
            {
                inaccessibleCount++;
                hasPermissionIssue = true;
                continue;
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var info = new FileInfo(file);
                    if (!info.Exists)
                    {
                        continue;
                    }

                    long sizeBytes = info.Length;
                    exactSizeBytes += sizeBytes;
                    fileCount++;

                    bool shouldSample = sizeBytes > 1024L * 1024L || topItems.Count < 20;
                    if (!shouldSample)
                    {
                        continue;
                    }

                    var (category, severity, reason, isSelectable) = ClassifyFile(info.Extension);
                    var item = new DeepScanItem(
                        FullPath: info.FullName,
                        SizeBytes: sizeBytes,
                        Category: category,
                        Severity: severity,
                        Reason: reason,
                        IsSelectable: isSelectable);

                    topItems.Enqueue(item, sizeBytes);
                    if (topItems.Count > 20)
                    {
                        topItems.Dequeue();
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    inaccessibleCount++;
                    hasPermissionIssue = true;
                }
                catch (IOException)
                {
                    inaccessibleCount++;
                    hasPermissionIssue = true;
                }
            }

            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(currentDir, "*", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                inaccessibleCount++;
                hasPermissionIssue = true;
                continue;
            }
            catch (IOException)
            {
                inaccessibleCount++;
                hasPermissionIssue = true;
                continue;
            }

            foreach (var subDir in subDirs)
            {
                ct.ThrowIfCancellationRequested();
                pendingDirs.Push(subDir);
            }
        }

        var samples = new List<DeepScanItem>(topItems.Count);
        while (topItems.Count > 0)
        {
            samples.Add(topItems.Dequeue());
        }

        samples.Reverse();

        return new DeepScanResult(
            Title: string.IsNullOrWhiteSpace(title) ? "深度扫描" : title,
            RootPath: rootPath,
            ExactSizeBytes: exactSizeBytes,
            FileCount: fileCount,
            DirectoryCount: directoryCount,
            InaccessibleCount: inaccessibleCount,
            HasPermissionIssue: hasPermissionIssue,
            Items: samples);
    }

    private static (string category, DeepScanSeverity severity, string reason, bool isSelectable) ClassifyFile(string extension)
    {
        string ext = extension?.ToLowerInvariant() ?? string.Empty;

        return ext switch
        {
            ".tmp" or ".cache" or ".log" => ("临时/缓存", DeepScanSeverity.Safe, "运行产生的临时文件，通常可清理", true),
            ".dmp" => ("崩溃转储", DeepScanSeverity.Caution, "排障后通常可清理，建议先预览", true),
            ".db" or ".sqlite" or ".sqlite3" => ("应用数据库", DeepScanSeverity.Danger, "疑似业务数据，默认不建议自动处理", false),
            _ => ("未知文件", DeepScanSeverity.Caution, "来源未知，建议人工确认后处理", true)
        };
    }
}

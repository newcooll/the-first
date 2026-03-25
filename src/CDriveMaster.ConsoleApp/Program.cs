using System;
using System.Linq;
using CDriveMaster.Core.Detectors;
using CDriveMaster.Core.Executors;
using CDriveMaster.Core.Guards;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Providers;
using CDriveMaster.Core.Services;

namespace CDriveMaster.ConsoleApp;

internal class Program
{
    private static void Main(string[] args)
    {
        bool isApply = args.Any(x => string.Equals(x, "--apply", StringComparison.OrdinalIgnoreCase));
        bool safeAutoOnly = args.Any(x => string.Equals(x, "--safe-auto-only", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine("=== CDriveMaster v2 Cleanup CLI ===\n");

        var detector = new WeChatDetector();
        var bucketBuilder = new BucketBuilder();
        var provider = new WeChatCleanupProvider(detector, bucketBuilder);
        var guard = new PreflightGuard();
        var jobId = Guid.NewGuid().ToString("N");
        var dryRunExecutor = new DryRunExecutor(guard, jobId);
        var cleanupExecutor = new CleanupExecutor(guard, jobId);
        var pipeline = new CleanupPipeline(dryRunExecutor, cleanupExecutor);

        Console.WriteLine("[Step 1] Detecting WeChat...");
        var detectResult = detector.Detect();
        Console.WriteLine($"Found: {detectResult.Found}");
        Console.WriteLine($"Source: {detectResult.Source}");
        Console.WriteLine($"Path: {detectResult.BasePath}");
        Console.WriteLine($"Reason: {detectResult.Reason}\n");

        if (!detectResult.Found)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("WeChat data directory not found. Exiting.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine("[Step 2] Building Cleanup Buckets...");
        var buckets = provider.GetBuckets().AsEnumerable();

        if (safeAutoOnly)
        {
            buckets = buckets.Where(x => x.RiskLevel == RiskLevel.SafeAuto);
        }

        var bucketList = buckets.ToList();

        if (bucketList.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("No eligible buckets found after filtering.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine($"Generated {bucketList.Count} logical buckets:");
        foreach (var bucket in bucketList)
        {
            double sizeMb = bucket.EstimatedSizeBytes / 1024.0 / 1024.0;
            Console.WriteLine($"  -> [{bucket.RiskLevel}] {bucket.Description} | Entries: {bucket.Entries.Count} | Size: {sizeMb:F2} MB");
        }

        Console.WriteLine();
        if (isApply)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("正在执行物理清理...");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("当前为 DryRun 空跑模式，不物理删除文件");
            Console.ResetColor();
        }

        Console.WriteLine("\n[Step 3] Executing Pipeline...");
        var results = pipeline.Execute(bucketList, isApply);

        Console.WriteLine("\n[Step 4] Bucket Results:");
        foreach (var result in results)
        {
            double estimatedMb = result.Bucket.EstimatedSizeBytes / 1024.0 / 1024.0;
            double reclaimedMb = result.ReclaimedSizeBytes / 1024.0 / 1024.0;
            Console.WriteLine(
                $"[{result.Bucket.RiskLevel}] {result.Bucket.Description} | Status: {result.FinalStatus} | Estimated: {estimatedMb:F2} MB | Reclaimed: {reclaimedMb:F2} MB | Success: {result.SuccessCount} | Failed: {result.FailedCount}");
        }

        var totalLogs = results.Sum(x => x.Logs.Count);
        Console.WriteLine($"\nCompleted. Total buckets: {results.Count}, total audit logs: {totalLogs}");
    }
}

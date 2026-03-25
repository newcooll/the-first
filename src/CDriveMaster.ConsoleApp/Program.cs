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
        bool isAnalyzeSystem = args.Contains("--analyze-system");
        bool isApply = args.Any(x => string.Equals(x, "--apply", StringComparison.OrdinalIgnoreCase));
        bool safeAutoOnly = args.Any(x => string.Equals(x, "--safe-auto-only", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine("=== CDriveMaster v2 Cleanup CLI ===\n");

        if (isAnalyzeSystem)
        {
            if (!PlatformProbe.IsElevated)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("系统维护分析需要管理员权限，请以 Administrator 身份重新运行此程序！");
                Console.ResetColor();
                return;
            }

            var operationId = Guid.NewGuid().ToString("N");
            var runner = new DismCommandRunner();
            var analyzer = new DismAnalyzer(runner);

            Console.WriteLine("正在调用 DISM 分析系统组件存储，这可能需要几分钟时间，请稍候...");

            var report = analyzer.AnalyzeAsync(operationId).GetAwaiter().GetResult();

            Console.WriteLine("\n=== 系统组件存储分析报告 ===");
            Console.WriteLine($"操作ID: {report.OperationId}");
            Console.WriteLine($"名称: {report.Name}");
            Console.WriteLine($"风险级别: {report.Risk}");
            Console.WriteLine($"组件库总大小(GB): {ToGb(report.ActualSizeBytes):F2}");
            Console.WriteLine($"备份与禁用功能大小(GB): {ToGb(report.BackupsAndDisabledFeaturesBytes):F2}");
            Console.WriteLine($"缓存与临时数据大小(GB): {ToGb(report.CacheAndTemporaryDataBytes):F2}");
            Console.WriteLine($"预计可回收大小(GB): {ToGb(report.EstimatedReclaimableBytes):F2}");
            Console.WriteLine($"可回收包数量: {report.ReclaimablePackageCount}");
            Console.WriteLine($"建议清理: {(report.CleanupRecommended ? "Yes" : "No")}");
            return;
        }

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

    private static double ToGb(long bytes)
    {
        return bytes / 1024.0 / 1024.0 / 1024.0;
    }
}

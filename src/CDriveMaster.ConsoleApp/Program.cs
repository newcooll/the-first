using System;
using System.Text.Json;
using CDriveMaster.Core.Detectors;
using CDriveMaster.Core.Executors;
using CDriveMaster.Core.Providers;
using CDriveMaster.Core.Services;

namespace CDriveMaster.ConsoleApp;

internal class Program
{
    private static void Main()
    {
        Console.WriteLine("=== CDriveMaster v2 DryRun Pipeline Test ===\n");

        var detector = new WeChatDetector();
        var bucketBuilder = new BucketBuilder();
        var provider = new WeChatCleanupProvider(detector, bucketBuilder);
        var executor = new DryRunExecutor();

        Console.WriteLine("[Step 1] Detecting WeChat...");
        var detectResult = detector.Detect();
        Console.WriteLine($"Found: {detectResult.Found}");
        Console.WriteLine($"Source: {detectResult.Source}");
        Console.WriteLine($"Path: {detectResult.BasePath}");
        Console.WriteLine($"Reason: {detectResult.Reason}\n");

        if (!detectResult.Found)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("WeChat data directory not found. Exiting test.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine("[Step 2] Building Cleanup Buckets...");
        var buckets = provider.GetBuckets();
        Console.WriteLine($"Generated {buckets.Count} logical buckets:");
        foreach (var bucket in buckets)
        {
            double sizeMb = bucket.EstimatedSizeBytes / 1024.0 / 1024.0;
            Console.WriteLine($"  -> [{bucket.RiskLevel}] {bucket.Description} | Entries: {bucket.Entries.Count} | Size: {sizeMb:F2} MB");
        }

        Console.WriteLine();
        Console.WriteLine("[Step 3] Executing DryRun Pipeline...");
        var logs = executor.Execute(buckets);

        Console.WriteLine("\n[Step 4] Audit Log Sample (First 5 records):");
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

        int sampleCount = Math.Min(5, logs.Count);
        for (int i = 0; i < sampleCount; i++)
        {
            Console.WriteLine(JsonSerializer.Serialize(logs[i], jsonOptions));
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nDryRun completed successfully. Total audit logs generated: {logs.Count}");
        Console.ResetColor();
    }
}

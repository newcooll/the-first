using System;
using System.Collections.Generic;
using System.IO;
using CDriveMaster.Core.Interfaces;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;

namespace CDriveMaster.Core.Providers;

public class WeChatCleanupProvider : ICleanupProvider
{
    private readonly IAppDetector detector;
    private readonly BucketBuilder bucketBuilder;

    public WeChatCleanupProvider(IAppDetector detector, BucketBuilder bucketBuilder)
    {
        this.detector = detector;
        this.bucketBuilder = bucketBuilder;
    }

    public string AppName => detector.AppName;

    public IReadOnlyList<CleanupBucket> GetBuckets()
    {
        var detection = detector.Detect();
        if (!detection.Found || string.IsNullOrWhiteSpace(detection.BasePath))
        {
            return Array.Empty<CleanupBucket>();
        }

        var buckets = new List<CleanupBucket>();
        var basePath = detection.BasePath;

        foreach (var wxidDirectory in SafeEnumerateDirectories(basePath, "wxid_*"))
        {
            var cachePath = Path.Combine(wxidDirectory, "FileStorage", "Cache");
            var cacheBucket = bucketBuilder.BuildBucket(
                cachePath,
                AppName,
                RiskLevel.SafeAuto,
                CleanupAction.DeleteToRecycleBin,
                "WeChat cache files.",
                "Cache");
            if (cacheBucket is not null)
            {
                buckets.Add(cacheBucket);
            }

            var videoPath = Path.Combine(wxidDirectory, "FileStorage", "Video");
            var videoBucket = bucketBuilder.BuildBucket(
                videoPath,
                AppName,
                RiskLevel.SafeWithPreview,
                CleanupAction.DeleteToRecycleBin,
                "WeChat video files.",
                "Video");
            if (videoBucket is not null)
            {
                buckets.Add(videoBucket);
            }
        }

        var appletPath = Path.Combine(basePath, "Applet");
        var appletBucket = bucketBuilder.BuildBucket(
            appletPath,
            AppName,
            RiskLevel.SafeWithPreview,
            CleanupAction.DeleteToRecycleBin,
            "WeChat mini-program cache.",
            "Applet");
        if (appletBucket is not null)
        {
            buckets.Add(appletBucket);
        }

        return buckets.AsReadOnly();
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root, string pattern)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(pattern))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.EnumerateDirectories(root, pattern, SearchOption.TopDirectoryOnly);
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }
}

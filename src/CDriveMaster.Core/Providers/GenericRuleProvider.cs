using System;
using System.Collections.Generic;
using System.IO;
using CDriveMaster.Core.Interfaces;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;

namespace CDriveMaster.Core.Providers;

public class GenericRuleProvider : ICleanupProvider
{
    private readonly CleanupRule _rule;
    private readonly IAppDetector _detector;
    private readonly BucketBuilder _bucketBuilder;

    public GenericRuleProvider(CleanupRule rule, IAppDetector detector, BucketBuilder bucketBuilder)
    {
        _rule = rule;
        _detector = detector;
        _bucketBuilder = bucketBuilder;
    }

    public string AppName => _rule.AppName;

    public IReadOnlyList<CleanupBucket> GetBuckets()
    {
        var detection = _detector.Detect();
        if (!detection.Found || string.IsNullOrWhiteSpace(detection.BasePath))
        {
            return Array.Empty<CleanupBucket>();
        }

        var buckets = new List<CleanupBucket>();
        string basePath = detection.BasePath;

        foreach (var target in _rule.Targets)
        {
            if (UseWeChatWxidExpansion(target))
            {
                foreach (var wxidPath in SafeEnumerateDirectories(basePath, "wxid_*"))
                {
                    var targetPath = Path.Combine(wxidPath, NormalizeRelativePath(target.BaseFolder));
                    TryBuildAndAddBucket(buckets, targetPath, target);
                }

                continue;
            }

            var directPath = Path.Combine(basePath, NormalizeRelativePath(target.BaseFolder));
            TryBuildAndAddBucket(buckets, directPath, target);
        }

        return buckets.AsReadOnly();
    }

    private void TryBuildAndAddBucket(List<CleanupBucket> buckets, string targetPath, TargetRule target)
    {
        var bucket = _bucketBuilder.BuildBucket(
            targetPath,
            AppName,
            target.RiskLevel,
            _rule.DefaultAction,
            BuildDescription(target),
            target.Kind);

        if (bucket is not null)
        {
            buckets.Add(bucket);
        }
    }

    private string BuildDescription(TargetRule target)
    {
        if (string.IsNullOrWhiteSpace(_rule.Description))
        {
            return target.Kind;
        }

        return $"{_rule.Description} ({target.Kind})";
    }

    private bool UseWeChatWxidExpansion(TargetRule target)
    {
        if (!string.Equals(_rule.AppName, "WeChat", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = NormalizeRelativePath(target.BaseFolder);
        return normalized.StartsWith("FileStorage", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        return relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
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
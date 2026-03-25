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
            string normalizedTarget = NormalizeRelativePath(target.BaseFolder);

            if (HasWildcardPrefix(normalizedTarget))
            {
                string relativePath = TrimWildcardPrefix(normalizedTarget);

                foreach (var userPath in SafeEnumerateDirectories(basePath))
                {
                    var targetPath = string.IsNullOrWhiteSpace(relativePath)
                        ? userPath
                        : Path.Combine(userPath, relativePath);

                    if (!Directory.Exists(targetPath))
                    {
                        continue;
                    }

                    TryBuildAndAddBucket(buckets, targetPath, target);
                }

                continue;
            }

            var directPath = Path.Combine(basePath, normalizedTarget);
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

    private static bool HasWildcardPrefix(string path)
    {
        return path.StartsWith($"*{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
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

    private static string TrimWildcardPrefix(string path)
    {
        if (!HasWildcardPrefix(path))
        {
            return path;
        }

        return path.Substring(2);
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly);
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
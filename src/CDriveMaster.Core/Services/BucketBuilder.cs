using System;
using System.Collections.Generic;
using System.IO;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Services;

public class BucketBuilder
{
    public CleanupBucket? BuildBucket(
        string rootPath,
        string appName,
        RiskLevel risk,
        CleanupAction action,
        string description,
        string categoryName)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        var resolvedRoot = FsEntry.Resolve(rootPath) as DirectoryInfo;
        if (resolvedRoot is null)
        {
            return null;
        }

        try
        {
            if (FsEntry.IsReparsePoint(resolvedRoot))
            {
                return null;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        var entries = new List<CleanupEntry>();
        long estimatedSizeBytes = 0;

        foreach (var fileSystemEntry in SafeWalker.EnumerateEntriesSafe(resolvedRoot.FullName))
        {
            bool isDirectory = fileSystemEntry is DirectoryInfo;
            long sizeBytes = 0;
            DateTime lastWriteTimeUtc;

            try
            {
                lastWriteTimeUtc = fileSystemEntry.LastWriteTimeUtc;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            if (!isDirectory)
            {
                try
                {
                    sizeBytes = ((FileInfo)fileSystemEntry).Length;
                    estimatedSizeBytes += sizeBytes;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }
            }

            entries.Add(new CleanupEntry(
                fileSystemEntry.FullName,
                isDirectory,
                sizeBytes,
                lastWriteTimeUtc,
                categoryName));
        }

        return new CleanupBucket(
            BucketId: Guid.NewGuid().ToString("N"),
            Category: categoryName,
            RootPath: resolvedRoot.FullName,
            AppName: appName,
            RiskLevel: risk,
            SuggestedAction: action,
            Description: description,
            EstimatedSizeBytes: estimatedSizeBytes,
            Entries: entries.AsReadOnly(),
            AllowedRoots: new[] { resolvedRoot.FullName });
    }
}

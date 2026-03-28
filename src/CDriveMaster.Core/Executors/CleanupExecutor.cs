using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CDriveMaster.Core.Guards;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Services;

namespace CDriveMaster.Core.Executors;

public class CleanupExecutor
{
    private readonly PreflightGuard guard;
    private readonly string jobId;
    private readonly ICleanupDeleteBackend deleteBackend;

    public CleanupExecutor(PreflightGuard guard, string jobId, ICleanupDeleteBackend? deleteBackend = null)
    {
        this.guard = guard;
        this.jobId = jobId;
        this.deleteBackend = deleteBackend ?? new WindowsCleanupDeleteBackend();
    }

    public IReadOnlyList<AuditLogItem> Execute(IReadOnlyList<CleanupBucket> buckets)
    {
        return Execute(buckets, allowTrustedExactFileFastPath: false);
    }

    public IReadOnlyList<AuditLogItem> Execute(
        IReadOnlyList<CleanupBucket> buckets,
        bool allowTrustedExactFileFastPath)
    {
        var logs = new List<AuditLogItem>();

        foreach (var bucket in buckets)
        {
            bool canUseTrustedFastPath = allowTrustedExactFileFastPath && IsExactFileScopedBucket(bucket);
            var executableEntries = new List<CleanupEntry>();
            foreach (var entry in bucket.Entries)
            {
                var preflight = canUseTrustedFastPath
                    ? guard.CheckPathForExecution(entry.Path, entry.IsDirectory, bucket.AllowedRoots ?? new[] { bucket.RootPath })
                    : guard.CheckPath(entry.Path, bucket.AllowedRoots ?? new[] { bucket.RootPath });
                if (!preflight.Passed)
                {
                    logs.Add(new AuditLogItem(
                        JobId: jobId,
                        BucketId: bucket.BucketId,
                        TimestampUtc: DateTime.UtcNow,
                        TargetPath: entry.Path,
                        TargetSizeBytes: entry.SizeBytes,
                        Action: bucket.SuggestedAction,
                        Risk: bucket.RiskLevel,
                        AppName: bucket.AppName,
                        Reason: preflight.Reason,
                        Status: ExecutionStatus.Blocked,
                        ErrorMessage: null));
                    continue;
                }
                executableEntries.Add(entry);
            }

            if (executableEntries.Count == 0)
            {
                continue;
            }

            var deleteOperations = canUseTrustedFastPath
                ? BuildDeleteOperations(executableEntries, bucket.RootPath)
                : executableEntries
                    .Select(entry => new DeleteOperation(entry, new[] { entry }))
                    .ToList();

            foreach (var result in ExecuteDeleteOperations(deleteOperations, bucket))
            {
                if (result.Status == ExecutionStatus.Skipped && !string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    Debug.WriteLine($"Cleanup skipped: {result.Path}. {result.ErrorMessage}");
                }

                logs.Add(new AuditLogItem(
                    JobId: jobId,
                    BucketId: bucket.BucketId,
                    TimestampUtc: DateTime.UtcNow,
                    TargetPath: result.Path,
                    TargetSizeBytes: result.SizeBytes,
                    Action: bucket.SuggestedAction,
                    Risk: bucket.RiskLevel,
                    AppName: bucket.AppName,
                    Reason: string.IsNullOrWhiteSpace(result.DetailMessage)
                        ? "Passed"
                        : result.DetailMessage,
                    Status: result.Status,
                    ErrorMessage: result.ErrorMessage));
            }
        }

        return logs.AsReadOnly();
    }

    private IReadOnlyList<DeleteLogResult> ExecuteDeleteOperations(
        IReadOnlyList<DeleteOperation> operations,
        CleanupBucket bucket)
    {
        var operationByPath = operations.ToDictionary(
            operation => NormalizePath(operation.Entry.Path),
            StringComparer.OrdinalIgnoreCase);
        var results = new List<DeleteLogResult>(operations.Sum(operation => operation.CoveredEntries.Count));

        foreach (var deleteResult in deleteBackend.DeleteMany(operations.Select(operation => operation.Entry).ToArray(), bucket.SuggestedAction))
        {
            if (!operationByPath.TryGetValue(NormalizePath(deleteResult.Path), out var operation))
            {
                continue;
            }

                if (operation.CoveredEntries.Count == 1)
                {
                    var coveredEntry = operation.CoveredEntries[0];
                    results.Add(new DeleteLogResult(
                        coveredEntry.Path,
                        coveredEntry.SizeBytes,
                        deleteResult.Status,
                        deleteResult.ErrorMessage,
                        deleteResult.DetailMessage));
                    continue;
                }

            if (deleteResult.Status == ExecutionStatus.Success)
            {
                foreach (var coveredEntry in operation.CoveredEntries)
                {
                    results.Add(new DeleteLogResult(
                        coveredEntry.Path,
                        coveredEntry.SizeBytes,
                        ExecutionStatus.Success,
                        null,
                        deleteResult.DetailMessage));
                }

                continue;
            }

            foreach (var fallbackResult in deleteBackend.DeleteMany(operation.CoveredEntries, bucket.SuggestedAction))
            {
                var coveredEntry = operation.CoveredEntries.FirstOrDefault(entry =>
                    string.Equals(entry.Path, fallbackResult.Path, StringComparison.OrdinalIgnoreCase));
                results.Add(new DeleteLogResult(
                    fallbackResult.Path,
                    coveredEntry?.SizeBytes ?? 0,
                    fallbackResult.Status,
                    string.IsNullOrWhiteSpace(deleteResult.ErrorMessage)
                        ? fallbackResult.ErrorMessage
                        : string.IsNullOrWhiteSpace(fallbackResult.ErrorMessage)
                            ? deleteResult.ErrorMessage
                            : $"{deleteResult.ErrorMessage} | {fallbackResult.ErrorMessage}",
                    string.IsNullOrWhiteSpace(fallbackResult.DetailMessage)
                        ? deleteResult.DetailMessage
                        : fallbackResult.DetailMessage));
            }
        }

        return results;
    }

    private static IReadOnlyList<DeleteOperation> BuildDeleteOperations(
        IReadOnlyList<CleanupEntry> entries,
        string? bucketRootPath)
    {
        if (entries.Count < 2)
        {
            return entries
                .Select(entry => new DeleteOperation(entry, new[] { entry }))
                .ToList();
        }

        var coveredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var operations = new List<DeleteOperation>();
        string? normalizedBucketRoot = string.IsNullOrWhiteSpace(bucketRootPath) ? null : NormalizePath(bucketRootPath);

        var parentGroups = entries
            .Where(entry => !entry.IsDirectory)
            .SelectMany(entry => EnumerateCandidateDirectories(entry.Path, normalizedBucketRoot)
                .Select(directoryPath => new { DirectoryPath = directoryPath, Entry = entry }))
            .GroupBy(item => item.DirectoryPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                DirectoryPath = group.Key,
                Entries = group
                    .Select(item => item.Entry)
                    .DistinctBy(entry => NormalizePath(entry.Path), StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .Where(group => !string.IsNullOrWhiteSpace(group.DirectoryPath) && group.Entries.Count >= 2)
            .OrderByDescending(group => group.Entries.Count)
            .ThenBy(group => group.DirectoryPath!.Length)
            .ToList();

        foreach (var parentGroup in parentGroups)
        {
            string directoryPath = parentGroup.DirectoryPath!;
            var candidateEntries = parentGroup.Entries
                .Where(entry => !coveredPaths.Contains(NormalizePath(entry.Path)))
                .ToList();
            if (candidateEntries.Count < 2)
            {
                continue;
            }

            if (!CanCollapseDirectory(directoryPath, candidateEntries))
            {
                continue;
            }

            operations.Add(new DeleteOperation(
                new CleanupEntry(
                    Path: directoryPath,
                    IsDirectory: true,
                    SizeBytes: candidateEntries.Sum(entry => entry.SizeBytes),
                    LastWriteTimeUtc: candidateEntries.Max(entry => entry.LastWriteTimeUtc),
                    Category: candidateEntries[0].Category),
                candidateEntries));

            foreach (var entry in candidateEntries)
            {
                coveredPaths.Add(NormalizePath(entry.Path));
            }
        }

        foreach (var entry in entries)
        {
            if (coveredPaths.Contains(NormalizePath(entry.Path)))
            {
                continue;
            }

            operations.Add(new DeleteOperation(entry, new[] { entry }));
        }

        return operations;
    }

    private static bool CanCollapseDirectory(
        string directoryPath,
        IReadOnlyList<CleanupEntry> candidateEntries)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return false;
        }

        var directoryInfo = new DirectoryInfo(directoryPath);
        if (FsEntry.IsReparsePoint(directoryInfo))
        {
            return false;
        }

        var candidatePaths = candidateEntries
            .Select(entry => NormalizePath(entry.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int discoveredFileCount = 0;

        var pending = new Stack<DirectoryInfo>();
        pending.Push(directoryInfo);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            FileSystemInfo[] children;
            try
            {
                children = current.GetFileSystemInfos();
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }

            foreach (var child in children)
            {
                try
                {
                    if (FsEntry.IsReparsePoint(child))
                    {
                        return false;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
                catch (IOException)
                {
                    return false;
                }

                if (child is DirectoryInfo childDirectory)
                {
                    pending.Push(childDirectory);
                    continue;
                }

                string normalizedFilePath = NormalizePath(child.FullName);
                discoveredFileCount++;
                if (!candidatePaths.Contains(normalizedFilePath))
                {
                    return false;
                }
            }
        }

        return discoveredFileCount == candidatePaths.Count;
    }

    private static IEnumerable<string> EnumerateCandidateDirectories(string filePath, string? normalizedBucketRoot)
    {
        string? current = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrWhiteSpace(current))
        {
            string normalizedCurrent = NormalizePath(current);
            string? root = Path.GetPathRoot(normalizedCurrent);
            if (!string.IsNullOrWhiteSpace(root)
                && string.Equals(
                    normalizedCurrent,
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            yield return normalizedCurrent;

            if (!string.IsNullOrWhiteSpace(normalizedBucketRoot)
                && string.Equals(normalizedCurrent, normalizedBucketRoot, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            current = Path.GetDirectoryName(normalizedCurrent);
        }
    }

    private static bool IsExactFileScopedBucket(CleanupBucket bucket)
    {
        if (bucket.Entries.Count == 0 || bucket.Entries.Any(entry => entry.IsDirectory) || bucket.AllowedRoots is null)
        {
            return false;
        }

        var allowedRoots = bucket.AllowedRoots
            .Select(NormalizePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowedRoots.Count == 0)
        {
            return false;
        }

        foreach (var entry in bucket.Entries)
        {
            string normalizedEntryPath = NormalizePath(entry.Path);
            if (string.IsNullOrWhiteSpace(normalizedEntryPath) || !allowedRoots.Contains(normalizedEntryPath))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private sealed record DeleteOperation(CleanupEntry Entry, IReadOnlyList<CleanupEntry> CoveredEntries);

    private sealed record DeleteLogResult(string Path, long SizeBytes, ExecutionStatus Status, string? ErrorMessage, string? DetailMessage);
}

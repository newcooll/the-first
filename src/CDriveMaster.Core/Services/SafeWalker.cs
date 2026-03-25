using System;
using System.Collections.Generic;
using System.IO;

namespace CDriveMaster.Core.Services;

public static class FsEntry
{
    public static FileSystemInfo? Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            if (File.Exists(path))
            {
                return new FileInfo(path);
            }

            if (Directory.Exists(path))
            {
                return new DirectoryInfo(path);
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

        return null;
    }

    public static bool IsReparsePoint(FileSystemInfo entry)
    {
        return (entry.Attributes & FileAttributes.ReparsePoint) != 0;
    }
}

public static class SafeWalker
{
    public static long GetDirectorySizeSafe(string rootPath)
    {
        long totalBytes = 0;

        foreach (var entry in EnumerateEntriesSafe(rootPath))
        {
            if (entry is not FileInfo file)
            {
                continue;
            }

            try
            {
                totalBytes += file.Length;
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        return totalBytes;
    }

    public static IEnumerable<FileSystemInfo> EnumerateEntriesSafe(string rootPath)
    {
        var root = FsEntry.Resolve(rootPath) as DirectoryInfo;
        if (root is null)
        {
            yield break;
        }

        bool rootIsReparsePoint;
        try
        {
            rootIsReparsePoint = FsEntry.IsReparsePoint(root);
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }
        catch (IOException)
        {
            yield break;
        }

        if (rootIsReparsePoint)
        {
            yield break;
        }

        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<FileSystemInfo> children;

            try
            {
                children = current.EnumerateFileSystemInfos();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var child in children)
            {
                bool isReparsePoint;
                try
                {
                    isReparsePoint = FsEntry.IsReparsePoint(child);
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }

                if (isReparsePoint)
                {
                    continue;
                }

                yield return child;

                if (child is DirectoryInfo childDirectory)
                {
                    pending.Push(childDirectory);
                }
            }
        }
    }
}

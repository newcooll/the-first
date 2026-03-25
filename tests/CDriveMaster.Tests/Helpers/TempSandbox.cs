using System;
using System.IO;
using System.Text;

namespace CDriveMaster.Tests.Helpers;

public sealed class TempSandbox : IDisposable
{
    public string RootPath { get; }

    public TempSandbox(string? prefix = null)
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            "CDriveMaster.Tests",
            $"{prefix ?? "sandbox"}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(RootPath);
    }

    public string CreateDirectory(params string[] segments)
    {
        var path = Combine(segments);
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateFile(string relativePath, string content = "x")
    {
        var fullPath = Combine(relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return fullPath;
    }

    public string Combine(params string[] segments)
    {
        var all = new string[segments.Length + 1];
        all[0] = RootPath;
        Array.Copy(segments, 0, all, 1, segments.Length);
        return Path.Combine(all);
    }

    public string Combine(string relativePath)
    {
        return Path.Combine(RootPath, relativePath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
        catch
        {
        }
    }
}

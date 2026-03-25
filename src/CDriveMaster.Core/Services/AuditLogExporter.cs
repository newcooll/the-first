using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Services;

public sealed class AuditLogExporter
{
    public async Task ExportAsync(string appName, IEnumerable<BucketResult> results)
    {
        var executed = results
            .Where(x => x.FinalStatus != ExecutionStatus.Skipped)
            .ToList();

        if (executed.Count == 0)
        {
            return;
        }

        try
        {
            string safeName = string.IsNullOrWhiteSpace(appName) ? "Unknown" : appName;
            string fileName = $"Audit_{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            await WriteAuditFileAsync(fileName, executed);
        }
        catch (IOException ex)
        {
            Debug.WriteLine(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public async Task ExportSystemMaintenanceAsync(SystemMaintenanceResult result)
    {
        if (result.Status == ExecutionStatus.Skipped)
        {
            return;
        }

        try
        {
            string fileName = $"Audit_SystemMaintenance_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            await WriteAuditFileAsync(fileName, result);
        }
        catch (IOException ex)
        {
            Debug.WriteLine(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static async Task WriteAuditFileAsync(string fileName, object payload)
    {
        string logsPath = Path.Combine(AppContext.BaseDirectory, "Logs");
        Directory.CreateDirectory(logsPath);

        string fullPath = Path.Combine(logsPath, fileName);
        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(payload, options);
        await File.WriteAllTextAsync(fullPath, json);
    }
}
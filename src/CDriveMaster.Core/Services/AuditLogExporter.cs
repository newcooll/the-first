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
        var resultList = results?.ToList() ?? new List<BucketResult>();

        try
        {
            string safeName = string.IsNullOrWhiteSpace(appName) ? "Unknown" : appName;
            string fileName = $"Audit_{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            bool hasPhysicalDelete = resultList.Any(x =>
                x.FinalStatus == ExecutionStatus.Success ||
                x.FinalStatus == ExecutionStatus.PartialSuccess);

            var payload = new
            {
                SessionSummary = hasPhysicalDelete
                    ? "Files were physically deleted during this session."
                    : "No files were physically deleted during this session.",
                AppName = safeName,
                GeneratedAtLocal = DateTime.Now,
                Results = resultList
            };

            await WriteAuditFileAsync(fileName, payload);
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
        try
        {
            string fileName = $"Audit_SystemMaintenance_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            bool hasPhysicalDelete = result.Status == ExecutionStatus.Success ||
                                     result.Status == ExecutionStatus.PartialSuccess;

            var payload = new
            {
                SessionSummary = hasPhysicalDelete
                    ? "Files were physically deleted during this session."
                    : "No files were physically deleted during this session.",
                GeneratedAtLocal = DateTime.Now,
                Result = result
            };

            await WriteAuditFileAsync(fileName, payload);
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
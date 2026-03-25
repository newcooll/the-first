using System;
using System.Globalization;
using System.Text.RegularExpressions;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Parsers;

public static class AnalyzeComponentStoreParser
{
    public static SystemMaintenanceReport Parse(string rawOutput, string operationId)
    {
        var output = rawOutput ?? string.Empty;

        long actualSizeBytes = ParseSize(output, "Actual Size of Component Store");
        long backupsAndDisabledFeaturesBytes = ParseSize(output, "Backups and Disabled Features");
        long cacheAndTemporaryDataBytes = ParseSize(output, "Cache and Temporary Data");
        int reclaimablePackageCount = ParseInteger(output, "Number of Reclaimable Packages");
        bool cleanupRecommended = ParseBooleanYesNo(output, "Component Store Cleanup Recommended");

        return new SystemMaintenanceReport(
            OperationId: operationId,
            Name: "Windows Component Store (WinSxS)",
            Risk: RiskLevel.SafeWithPreview,
            ActualSizeBytes: actualSizeBytes,
            BackupsAndDisabledFeaturesBytes: backupsAndDisabledFeaturesBytes,
            CacheAndTemporaryDataBytes: cacheAndTemporaryDataBytes,
            EstimatedReclaimableBytes: backupsAndDisabledFeaturesBytes + cacheAndTemporaryDataBytes,
            ReclaimablePackageCount: reclaimablePackageCount,
            CleanupRecommended: cleanupRecommended,
            RequiresAdmin: true,
            RawOutput: output);
    }

    private static long ParseSize(string rawOutput, string label)
    {
        var pattern = $@"{Regex.Escape(label)}\s*:\s*([0-9]+(?:\.[0-9]+)?)\s*(Bytes|KB|MB|GB)";
        var match = Regex.Match(rawOutput, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return 0;
        }

        return ParseBytes(match.Groups[1].Value, match.Groups[2].Value);
    }

    private static int ParseInteger(string rawOutput, string label)
    {
        var pattern = $@"{Regex.Escape(label)}\s*:\s*(\d+)";
        var match = Regex.Match(rawOutput, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return 0;
        }

        return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;
    }

    private static bool ParseBooleanYesNo(string rawOutput, string label)
    {
        var pattern = $@"{Regex.Escape(label)}\s*:\s*(Yes|No)";
        var match = Regex.Match(rawOutput, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        return string.Equals(match.Groups[1].Value, "Yes", StringComparison.OrdinalIgnoreCase);
    }

    private static long ParseBytes(string value, string unit)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            return 0;
        }

        double multiplier = unit.ToUpperInvariant() switch
        {
            "BYTES" => 1d,
            "KB" => 1024d,
            "MB" => 1024d * 1024d,
            "GB" => 1024d * 1024d * 1024d,
            _ => 1d
        };

        return Convert.ToInt64(number * multiplier);
    }
}

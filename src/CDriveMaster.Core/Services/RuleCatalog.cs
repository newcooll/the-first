using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CDriveMaster.Core.Interfaces;
using CDriveMaster.Core.Models;
using CDriveMaster.Core.Providers;

namespace CDriveMaster.Core.Services;

public sealed class RuleCatalog
{
    private readonly IEnumerable<IAppDetector> detectors;
    private readonly BucketBuilder bucketBuilder;

    public RuleCatalog(IEnumerable<IAppDetector> detectors, BucketBuilder bucketBuilder)
    {
        this.detectors = detectors;
        this.bucketBuilder = bucketBuilder;
    }

    public IReadOnlyList<ICleanupProvider> GetAllProviders()
    {
        string rulesPath = Path.Combine(AppContext.BaseDirectory, "Rules");
        if (!Directory.Exists(rulesPath))
        {
            return Array.Empty<ICleanupProvider>();
        }

        var providers = new List<ICleanupProvider>();
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());

        foreach (var file in Directory.EnumerateFiles(rulesPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            CleanupRule? rule;

            try
            {
                var json = File.ReadAllText(file);
                rule = JsonSerializer.Deserialize<CleanupRule>(json, jsonOptions);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (JsonException)
            {
                continue;
            }

            if (rule is null || string.IsNullOrWhiteSpace(rule.AppName))
            {
                continue;
            }

            var detector = detectors.FirstOrDefault(d =>
                string.Equals(d.AppName, rule.AppName, StringComparison.OrdinalIgnoreCase));

            if (detector is null)
            {
                continue;
            }

            providers.Add(new GenericRuleProvider(rule, detector, bucketBuilder));
        }

        return providers.AsReadOnly();
    }
}
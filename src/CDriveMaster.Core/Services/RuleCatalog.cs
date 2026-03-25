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
    private readonly List<string> failedRuleErrors = new();

    public RuleCatalog(IEnumerable<IAppDetector> detectors, BucketBuilder bucketBuilder)
    {
        this.detectors = detectors;
        this.bucketBuilder = bucketBuilder;
    }

    public IReadOnlyList<string> FailedRuleErrors => failedRuleErrors.AsReadOnly();

    public IReadOnlyList<ICleanupProvider> GetAllProviders()
    {
        failedRuleErrors.Clear();

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
            try
            {
                var json = File.ReadAllText(file);
                var rule = JsonSerializer.Deserialize<CleanupRule>(json, jsonOptions);

                if (rule is null)
                {
                    throw new InvalidOperationException("规则反序列化结果为空。");
                }

                if (string.IsNullOrWhiteSpace(rule.AppName))
                {
                    throw new InvalidOperationException("规则缺少 AppName。");
                }

                if (rule.Targets is null || !rule.Targets.Any())
                {
                    throw new InvalidOperationException("规则缺少 Targets 或 Targets 为空。");
                }

                var detector = detectors.FirstOrDefault(d =>
                    string.Equals(d.AppName, rule.AppName, StringComparison.OrdinalIgnoreCase));

                if (detector is null)
                {
                    failedRuleErrors.Add($"文件 {Path.GetFileName(file)} 加载失败: 未找到匹配探测器 {rule.AppName}。");
                    continue;
                }

                providers.Add(new GenericRuleProvider(rule, detector, bucketBuilder));
            }
            catch (JsonException ex)
            {
                failedRuleErrors.Add($"文件 {Path.GetFileName(file)} 加载失败: {ex.Message}");
                continue;
            }
            catch (Exception ex)
            {
                failedRuleErrors.Add($"文件 {Path.GetFileName(file)} 加载失败: {ex.Message}");
                continue;
            }
        }

        return providers.AsReadOnly();
    }
}
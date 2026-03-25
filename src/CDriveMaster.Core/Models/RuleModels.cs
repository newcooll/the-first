using System.Collections.Generic;

namespace CDriveMaster.Core.Models;

public sealed record TargetRule
{
    public string BaseFolder { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public RiskLevel RiskLevel { get; init; }
}

public sealed record CleanupRule
{
    public string AppName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public CleanupAction DefaultAction { get; init; }

    public List<TargetRule> Targets { get; init; } = new();
}
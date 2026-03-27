using System.Collections.Generic;
using System;

namespace CDriveMaster.Core.Models;

public sealed record TargetRule
{
    public string BaseFolder { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public RiskLevel RiskLevel { get; init; }
}

public sealed record FastScanHint
{
    public List<string> HotPaths { get; init; } = new();

    public HeuristicSearchHint[]? HeuristicSearchHints { get; init; }

    public SearchHint[]? SearchHints { get; init; }

    public int MaxDepth { get; init; } = 2;

    public long MinSizeThreshold { get; init; } = 50L * 1024L * 1024L;

    public string Category { get; init; } = string.Empty;

    public bool IsExperimental { get; init; } = false;
}

public class HeuristicSearchHint
{
    public string Parent { get; init; } = string.Empty;

    public string[] AppTokens { get; init; } = Array.Empty<string>();

    public string[] CacheTokens { get; init; } = Array.Empty<string>();

    public string[] FileMarkersAny { get; init; } = Array.Empty<string>();

    public int MaxDepth { get; init; } = 3;

    public int ScoreThreshold { get; init; } = 5;

    public long MinCandidateBytes { get; init; } = 52428800;
}

public sealed record SearchHint
{
    public string Parent { get; init; } = string.Empty;

    public string[] DirectoryKeywords { get; init; } = Array.Empty<string>();

    public string[] ChildMarkersAny { get; init; } = Array.Empty<string>();

    public string[] FileMarkersAny { get; init; } = Array.Empty<string>();

    public int MaxDepth { get; init; } = 2;

    public long MinCandidateBytes { get; init; } = 52428800;
}

public class AppEvidenceScore
{
    public string AppId { get; set; } = string.Empty;

    public int TotalScore { get; set; }

    public List<string> MatchedEvidences { get; set; } = new();
}

public class ResidualFingerprint
{
    public string Parent { get; set; } = string.Empty;

    public List<string> PathKeywords { get; set; } = new();

    public int MaxDepth { get; set; } = 4;

    public long MinSizeBytes { get; set; } = 20971520;
}

public record ProbeTraceInfo(
    int EvidenceScore,
    List<string> MatchedEvidences,
    List<string> CandidateDirectories,
    List<string> VerifiedDirectories,
    List<string> RejectedDirectories,
    List<string> RejectReasons,
    List<string> MatchHistory,
    string FinalDecision)
{
    public ProbeTraceInfo(
        int evidenceScore,
        List<string> matchedEvidences,
        List<string> candidateDirectories,
        List<string> verifiedDirectories,
        List<string> rejectedDirectories,
        List<string> rejectReasons)
        : this(
            evidenceScore,
            matchedEvidences,
            candidateDirectories,
            verifiedDirectories,
            rejectedDirectories,
            rejectReasons,
            new List<string>(),
            string.Empty)
    {
    }
}

public sealed record FastScanFinding
{
    public string AppId { get; init; } = string.Empty;

    public string AppName => AppId;

    public long SizeBytes { get; init; }

    public long TotalSizeBytes => SizeBytes;

    public string Category { get; init; } = string.Empty;

    public string? PrimaryPath { get; init; }

    public string SourcePath { get; init; } = string.Empty;

    public bool IsExactSize { get; init; } = true;

    public string DisplaySize { get; init; } = string.Empty;

    public bool IsExperimental { get; init; }

    public ProbeTraceInfo Trace { get; init; } = new(
        0,
        new List<string>(),
        new List<string>(),
        new List<string>(),
        new List<string>(),
        new List<string>(),
        new List<string>(),
        string.Empty);

    public bool IsHotspot { get; init; }

    public bool IsResidual { get; init; }

    public bool IsHeuristicMatch { get; init; }

    public CleanupBucket? OriginalBucket { get; init; }
}

public sealed record CleanupRule
{
    public string AppName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public CleanupAction DefaultAction { get; init; }

    public List<TargetRule> Targets { get; init; } = new();

    public FastScanHint? FastScan { get; init; }

    public string[]? AppMatchKeywords { get; init; }

    public List<ResidualFingerprint>? ResidualFingerprints { get; init; }
}

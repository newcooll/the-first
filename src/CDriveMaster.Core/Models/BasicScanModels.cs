using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CDriveMaster.Core.Utilities;

namespace CDriveMaster.Core.Models;

public enum BasicScanActionType
{
    CleanSelected,
    OpenFolder,
    NavigateToAppCleanup,
    NavigateToSystemMaintenance,
    DoNotTouch
}

public sealed record CleanupRecommendation(
    string Title,
    string Reason,
    string ActionText
);

public partial class BasicScanItem : ObservableObject
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required string FullPath { get; init; }

    public required long SizeBytes { get; init; }

    public string DisplaySize => SizeFormatter.Format(SizeBytes);

    public required RiskLevel RiskLevel { get; init; }

    public required BasicScanActionType ActionType { get; init; }

    public required bool IsSelectable { get; init; }

    public CleanupBucket? OriginalBucket { get; init; }

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private CleanupRecommendation? recommendation;
}

public sealed class BasicScanGroup
{
    public required string GroupId { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public ObservableCollection<BasicScanItem> Items { get; } = new();
}

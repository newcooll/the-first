using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CDriveMaster.Core.Models;

namespace CDriveMaster.UI.ViewModels.Items;

public partial class PreviewItemViewModel : ObservableObject
{
    public PreviewItemViewModel(CleanupEntry entry)
    {
        Entry = entry;
    }

    public CleanupEntry Entry { get; }

    [ObservableProperty]
    private bool isSelected;

    public string FilePath => Entry.Path;

    public long SizeBytes => Entry.SizeBytes;

    public string FormattedSize => FormatBytes(SizeBytes);

    public string LastAccessTime => Entry.LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / 1024.0 / 1024.0:F2} MB";
        }

        return $"{bytes / 1024.0:F2} KB";
    }
}
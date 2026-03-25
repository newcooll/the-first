using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CDriveMaster.Core.Models;
using CDriveMaster.UI.ViewModels.Items;

namespace CDriveMaster.UI.ViewModels;

public partial class PreviewDialogViewModel : ObservableObject
{
    private bool suppressAllSelectedSync;

    public PreviewDialogViewModel(IEnumerable<CleanupEntry> entries)
    {
        foreach (var entry in entries)
        {
            var item = new PreviewItemViewModel(entry);
            item.PropertyChanged += OnItemPropertyChanged;
            Items.Add(item);
        }

        RecalculateStats();
    }

    public ObservableCollection<PreviewItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private bool isAllSelected;

    [ObservableProperty]
    private int selectedCount;

    [ObservableProperty]
    private long selectedTotalSize;

    [ObservableProperty]
    private bool isConfirmed;

    public event Action<bool>? RequestClose;

    [RelayCommand]
    private void Confirm()
    {
        IsConfirmed = true;
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        IsConfirmed = false;
        RequestClose?.Invoke(false);
    }

    public IReadOnlyList<CleanupEntry> GetSelectedEntries()
    {
        return Items
            .Where(x => x.IsSelected)
            .Select(x => x.Entry)
            .ToList();
    }

    partial void OnIsAllSelectedChanged(bool value)
    {
        if (suppressAllSelectedSync)
        {
            return;
        }

        suppressAllSelectedSync = true;
        try
        {
            foreach (var item in Items)
            {
                item.IsSelected = value;
            }
        }
        finally
        {
            suppressAllSelectedSync = false;
        }

        RecalculateStats();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(PreviewItemViewModel.IsSelected), StringComparison.Ordinal))
        {
            return;
        }

        RecalculateStats();
    }

    private void RecalculateStats()
    {
        SelectedCount = Items.Count(x => x.IsSelected);
        SelectedTotalSize = Items.Where(x => x.IsSelected).Sum(x => x.SizeBytes);

        bool allSelected = Items.Count != 0 && SelectedCount == Items.Count;
        if (IsAllSelected != allSelected)
        {
            suppressAllSelectedSync = true;
            IsAllSelected = allSelected;
            suppressAllSelectedSync = false;
        }
    }
}
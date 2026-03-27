using System;
using System.Linq;
using CDriveMaster.Core.Models;
using CDriveMaster.UI.ViewModels;
using FluentAssertions;
using Xunit;

namespace CDriveMaster.Tests.ViewModels;

public sealed class PreviewDialogViewModelTests
{
    [Fact]
    public void IsAllSelected_should_toggle_all_items_and_update_stats_once()
    {
        var entries = Enumerable.Range(0, 200)
            .Select(index => new CleanupEntry(
                Path: $@"C:\Temp\cache-{index:D3}.bin",
                IsDirectory: false,
                SizeBytes: 1024 + index,
                LastWriteTimeUtc: DateTime.UtcNow,
                Category: "Cache"))
            .ToArray();

        var vm = new PreviewDialogViewModel(entries);

        vm.IsAllSelected = true;

        vm.SelectedCount.Should().Be(entries.Length);
        vm.SelectedTotalSize.Should().Be(entries.Sum(entry => entry.SizeBytes));
        vm.Items.Should().OnlyContain(item => item.IsSelected);
    }
}

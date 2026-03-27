using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CDriveMaster.Core.Models;
using CDriveMaster.UI.ViewModels;
using CDriveMaster.UI.Views;

namespace CDriveMaster.UI.Services;

public sealed class WpfPreviewDialogService : IPreviewDialogService
{
    public async Task<IEnumerable<CleanupEntry>> ShowPreviewAsync(string title, IEnumerable<CleanupEntry> entries, string? summary = null)
    {
        var app = Application.Current;
        if (app is null)
        {
            return Enumerable.Empty<CleanupEntry>();
        }

        return await app.Dispatcher.InvokeAsync(() =>
        {
            var entryList = entries.ToList();
            var vm = new PreviewDialogViewModel(entryList, summary);
            var window = new PreviewDialogWindow
            {
                Title = title,
                DataContext = vm,
                Owner = app.MainWindow
            };

            vm.RequestClose += accepted =>
            {
                window.DialogResult = accepted;
                window.Close();
            };

            bool? dialogResult = window.ShowDialog();
            if (dialogResult == true && vm.IsConfirmed)
            {
                return vm.GetSelectedEntries().AsEnumerable();
            }

            return Enumerable.Empty<CleanupEntry>();
        });
    }
}

using System.Windows;
using System.Threading.Tasks;

namespace CDriveMaster.UI.Services;

public sealed class MessageBoxDialogService : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message)
    {
        var result = MessageBox.Show(
            message,
            title,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        return Task.FromResult(result == MessageBoxResult.OK || result == MessageBoxResult.Yes);
    }

    public Task ShowInfoAsync(string title, string message)
    {
        MessageBox.Show(
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        return Task.CompletedTask;
    }

    public Task ShowErrorAsync(string title, string message)
    {
        MessageBox.Show(
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        return Task.CompletedTask;
    }
}

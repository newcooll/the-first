using System.Threading.Tasks;

namespace CDriveMaster.UI.Services;

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message);

    Task ShowInfoAsync(string title, string message);

    Task ShowErrorAsync(string title, string message);
}

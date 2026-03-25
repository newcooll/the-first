using System.Threading.Tasks;
using CDriveMaster.UI.Services;

namespace CDriveMaster.Tests.Fakes;

public sealed class FakeDialogService : IDialogService
{
    public bool ConfirmResult { get; set; } = true;

    public bool WasConfirmCalled { get; private set; }

    public bool WasShowInfoCalled { get; private set; }

    public bool WasShowErrorCalled { get; private set; }

    public string LastMessage { get; private set; } = string.Empty;

    public Task<bool> ConfirmAsync(string title, string message)
    {
        WasConfirmCalled = true;
        LastMessage = message;
        return Task.FromResult(ConfirmResult);
    }

    public Task ShowInfoAsync(string title, string message)
    {
        WasShowInfoCalled = true;
        LastMessage = message;
        return Task.CompletedTask;
    }

    public Task ShowErrorAsync(string title, string message)
    {
        WasShowErrorCalled = true;
        LastMessage = message;
        return Task.CompletedTask;
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;

namespace CDriveMaster.UI.Services;

public sealed class NavigationService : INavigationService
{
    public event Func<string, Task>? AppCleanupRequested;

    public void NavigateToAppCleanup(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return;
        }

        _ = RaiseAppCleanupRequestedAsync(appName);
    }

    private async Task RaiseAppCleanupRequestedAsync(string appName)
    {
        var handlers = AppCleanupRequested;
        if (handlers is null)
        {
            return;
        }

        var invocationList = handlers.GetInvocationList()
            .Cast<Func<string, Task>>()
            .ToArray();

        foreach (var handler in invocationList)
        {
            await handler(appName);
        }
    }
}

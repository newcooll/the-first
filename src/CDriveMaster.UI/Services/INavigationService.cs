using System;
using System.Threading.Tasks;

namespace CDriveMaster.UI.Services;

public interface INavigationService
{
    event Func<string, Task>? AppCleanupRequested;

    void NavigateToAppCleanup(string appName);
}

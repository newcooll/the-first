using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDriveMaster.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SystemMaintenanceViewModel systemMaintenanceViewModel;
    private readonly WeChatCleanupViewModel weChatCleanupViewModel;

    [ObservableProperty]
    private object currentViewModel;

    public MainViewModel(SystemMaintenanceViewModel systemMaintenanceViewModel, WeChatCleanupViewModel weChatCleanupViewModel)
    {
        this.systemMaintenanceViewModel = systemMaintenanceViewModel;
        this.weChatCleanupViewModel = weChatCleanupViewModel;
        CurrentViewModel = this.systemMaintenanceViewModel;
    }

    [RelayCommand]
    private void MapsToSystemMaintenance()
    {
        CurrentViewModel = systemMaintenanceViewModel;
    }

    [RelayCommand]
    private void MapsToWeChatCleanup()
    {
        CurrentViewModel = weChatCleanupViewModel;
    }
}

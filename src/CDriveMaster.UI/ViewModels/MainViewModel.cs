using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDriveMaster.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SystemMaintenanceViewModel systemMaintenanceViewModel;
    private readonly GenericCleanupViewModel genericCleanupViewModel;

    [ObservableProperty]
    private object currentViewModel;

    public MainViewModel(SystemMaintenanceViewModel systemMaintenanceViewModel, GenericCleanupViewModel genericCleanupViewModel)
    {
        this.systemMaintenanceViewModel = systemMaintenanceViewModel;
        this.genericCleanupViewModel = genericCleanupViewModel;
        CurrentViewModel = this.systemMaintenanceViewModel;
    }

    [RelayCommand]
    private void MapsToSystemMaintenance()
    {
        CurrentViewModel = systemMaintenanceViewModel;
    }

    [RelayCommand]
    private void MapsToAppCleanup()
    {
        CurrentViewModel = genericCleanupViewModel;
    }
}

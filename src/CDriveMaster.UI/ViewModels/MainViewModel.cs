using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDriveMaster.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SystemMaintenanceAnalysisViewModel systemMaintenanceViewModel;
    private readonly GenericCleanupViewModel genericCleanupViewModel;

    [ObservableProperty]
    private object currentViewModel;

    public MainViewModel(SystemMaintenanceAnalysisViewModel systemMaintenanceViewModel, GenericCleanupViewModel genericCleanupViewModel)
    {
        this.systemMaintenanceViewModel = systemMaintenanceViewModel;
        this.genericCleanupViewModel = genericCleanupViewModel;
        CurrentViewModel = this.systemMaintenanceViewModel;
    }

    [RelayCommand]
    private void NavigateToSystemMaintenance()
    {
        CurrentViewModel = systemMaintenanceViewModel;
    }

    [RelayCommand]
    private void MapsToSystemMaintenance()
    {
        NavigateToSystemMaintenance();
    }

    [RelayCommand]
    private void MapsToAppCleanup()
    {
        CurrentViewModel = genericCleanupViewModel;
    }
}

using CommunityToolkit.Mvvm.ComponentModel;

namespace CDriveMaster.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private object currentViewModel;

    public MainViewModel(SystemMaintenanceViewModel systemMaintenanceViewModel)
    {
        CurrentViewModel = systemMaintenanceViewModel;
    }
}

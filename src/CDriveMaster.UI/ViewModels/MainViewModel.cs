using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace CDriveMaster.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SystemMaintenanceAnalysisViewModel systemMaintenanceViewModel;
    private readonly GenericCleanupViewModel genericCleanupViewModel;

    [ObservableProperty]
    private object currentViewModel;

    public string AppVersion { get; }

    public MainViewModel(SystemMaintenanceAnalysisViewModel systemMaintenanceViewModel, GenericCleanupViewModel genericCleanupViewModel)
    {
        this.systemMaintenanceViewModel = systemMaintenanceViewModel;
        this.genericCleanupViewModel = genericCleanupViewModel;

        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        AppVersion = string.IsNullOrWhiteSpace(informationalVersion)
            ? "v0.0.0-unknown"
            : $"v{informationalVersion}";

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

    [RelayCommand]
    private void OpenRulesFolder()
    {
        OpenFolder("Rules");
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        OpenFolder("Logs");
    }

    [RelayCommand]
    private void OpenHelpDocs()
    {
        string docsPath = Path.Combine(AppContext.BaseDirectory, "docs");
        if (Directory.Exists(docsPath))
        {
            OpenFolder("docs");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/newcooll/the-first/wiki",
            UseShellExecute = true
        });
    }

    private static void OpenFolder(string folderName)
    {
        string fullPath = Path.Combine(AppContext.BaseDirectory, folderName);
        Directory.CreateDirectory(fullPath);

        Process.Start(new ProcessStartInfo
        {
            FileName = fullPath,
            UseShellExecute = true
        });
    }
}

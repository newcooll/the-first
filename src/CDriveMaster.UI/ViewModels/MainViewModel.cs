using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CDriveMaster.Core.Services;
using CDriveMaster.UI.Messages;
using CDriveMaster.UI.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace CDriveMaster.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SystemMaintenanceAnalysisViewModel systemMaintenanceViewModel;
    private readonly GenericCleanupViewModel genericCleanupViewModel;
    private readonly BasicScanDashboardViewModel basicScanDashboardViewModel;
    private readonly DiagnosticExporter diagExporter;
    private readonly IDialogService dialogService;

    [ObservableProperty]
    private object currentViewModel;

    public string AppVersion { get; }

    public MainViewModel(
        SystemMaintenanceAnalysisViewModel systemMaintenanceViewModel,
        GenericCleanupViewModel genericCleanupViewModel,
        BasicScanDashboardViewModel basicScanDashboardViewModel,
        DiagnosticExporter diagExporter,
        IDialogService dialogService)
    {
        this.systemMaintenanceViewModel = systemMaintenanceViewModel;
        this.genericCleanupViewModel = genericCleanupViewModel;
        this.basicScanDashboardViewModel = basicScanDashboardViewModel;
        this.diagExporter = diagExporter;
        this.dialogService = dialogService;

        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        AppVersion = string.IsNullOrWhiteSpace(informationalVersion)
            ? "v0.0.0-unknown"
            : $"v{informationalVersion}";

        CurrentViewModel = this.systemMaintenanceViewModel;

        WeakReferenceMessenger.Default.Register<NavigateToAppCleanupMessage>(
            this,
            async (_, message) =>
            {
                CurrentViewModel = genericCleanupViewModel;
                await genericCleanupViewModel.NavigateToAppAndScanAsync(message.AppId);
            });
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        try
        {
            string zipPath = await diagExporter.ExportAsync();
            await dialogService.ShowInfoAsync(
                "导出成功",
                $"诊断包已保存至桌面：{Environment.NewLine}{zipPath}{Environment.NewLine}{Environment.NewLine}请在提交反馈时附带此文件。");

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{zipPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            await dialogService.ShowErrorAsync("导出失败", ex.Message);
        }
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
    private void MapsToLargeFileAnalysis()
    {
        CurrentViewModel = basicScanDashboardViewModel;
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

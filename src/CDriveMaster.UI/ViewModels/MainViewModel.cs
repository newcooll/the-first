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
    private readonly HelpManualViewModel helpManualViewModel;
    private readonly DiagnosticExporter diagExporter;
    private readonly IDialogService dialogService;

    [ObservableProperty]
    private object currentViewModel;

    public string AppVersion { get; }

    public MainViewModel(
        SystemMaintenanceAnalysisViewModel systemMaintenanceViewModel,
        GenericCleanupViewModel genericCleanupViewModel,
        BasicScanDashboardViewModel basicScanDashboardViewModel,
        HelpManualViewModel helpManualViewModel,
        DiagnosticExporter diagExporter,
        IDialogService dialogService)
    {
        this.systemMaintenanceViewModel = systemMaintenanceViewModel;
        this.genericCleanupViewModel = genericCleanupViewModel;
        this.basicScanDashboardViewModel = basicScanDashboardViewModel;
        this.helpManualViewModel = helpManualViewModel;
        this.diagExporter = diagExporter;
        this.dialogService = dialogService;

        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        AppVersion = NormalizeAppVersion(informationalVersion);

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
                $"诊断包已保存到桌面：{Environment.NewLine}{zipPath}{Environment.NewLine}{Environment.NewLine}请在提交反馈时附带此文件。");

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
        CurrentViewModel = helpManualViewModel;
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

    internal static string NormalizeAppVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "v0.0.0-unknown";
        }

        string normalized = version.Trim();
        int buildMetadataIndex = normalized.IndexOf('+');
        if (buildMetadataIndex >= 0)
        {
            normalized = normalized[..buildMetadataIndex];
        }

        return normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"v{normalized}";
    }
}

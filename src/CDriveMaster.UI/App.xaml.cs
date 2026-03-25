using System;
using System.IO;
using System.Windows;
using CDriveMaster.Core.Detectors;
using CDriveMaster.Core.Executors;
using CDriveMaster.Core.Guards;
using CDriveMaster.Core.Interfaces;
using CDriveMaster.Core.Services;
using CDriveMaster.UI.Services;
using CDriveMaster.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CDriveMaster.UI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
	private IServiceProvider? serviceProvider;

	private void ConfigureServices(IServiceCollection services)
	{
		services.AddTransient<IAppDetector, WeChatDetector>();
		services.AddTransient<IAppDetector, QQDetector>();
		services.AddTransient<IAppDetector, ChromeDetector>();
		services.AddTransient<BucketBuilder>();
		services.AddTransient<RuleCatalog>();
		services.AddSingleton<IDialogService, MessageBoxDialogService>();
		services.AddSingleton<IPreviewDialogService, NoOpPreviewDialogService>();
		services.AddSingleton<PreflightGuard>();
		services.AddTransient(sp => new DryRunExecutor(
			sp.GetRequiredService<PreflightGuard>(),
			Guid.NewGuid().ToString("N")));
		services.AddTransient(sp => new CleanupExecutor(
			sp.GetRequiredService<PreflightGuard>(),
			Guid.NewGuid().ToString("N")));
		services.AddTransient<ICleanupPipeline, CleanupPipeline>();

		services.AddTransient<IDismCommandRunner, DismCommandRunner>();
		services.AddTransient<DismAnalyzer>();
		services.AddTransient<DismCleanupExecutor>();

		services.AddTransient<SystemMaintenanceViewModel>();
		services.AddTransient<GenericCleanupViewModel>();
		services.AddTransient<MainViewModel>();

		services.AddSingleton<MainWindow>();
	}

	protected override void OnStartup(StartupEventArgs e)
	{
		// Capture non-UI thread exceptions, such as background scan failures.
		AppDomain.CurrentDomain.UnhandledException += (_, args) =>
			LogAndNotify(args.ExceptionObject as Exception, "Fatal Domain Exception");

		// Capture UI thread exceptions to avoid hard crash without diagnostics.
		DispatcherUnhandledException += (_, args) =>
		{
			LogAndNotify(args.Exception, "Dispatcher Exception");
			args.Handled = true;
		};

		var services = new ServiceCollection();
		ConfigureServices(services);
		serviceProvider = services.BuildServiceProvider();

		var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
		mainWindow.DataContext = serviceProvider.GetRequiredService<MainViewModel>();
		mainWindow.Show();

		base.OnStartup(e);
	}

	private static void LogAndNotify(Exception? ex, string type)
	{
		try
		{
			var logDir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
				"CDriveMaster",
				"logs");
			Directory.CreateDirectory(logDir);

			var logFile = Path.Combine(logDir, $"crash_{DateTime.Now:yyyyMMdd}.log");
			var content =
				$"[{DateTime.Now:HH:mm:ss}] [{type}]{Environment.NewLine}" +
				$"{ex}{Environment.NewLine}" +
				$"{new string('-', 40)}{Environment.NewLine}";
			File.AppendAllText(logFile, content);

			MessageBox.Show(
				$"程序遇到未知错误，日志已记录至:{Environment.NewLine}{logFile}{Environment.NewLine}{Environment.NewLine}错误简述: {ex?.Message}",
				"CDriveMaster 异常捕获",
				MessageBoxButton.OK,
				MessageBoxImage.Error);
		}
		catch
		{
			// Never let logging failures trigger a second crash.
		}
	}
}


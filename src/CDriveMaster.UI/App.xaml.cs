using System;
using System.IO;
using System.Windows;

namespace CDriveMaster.UI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
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


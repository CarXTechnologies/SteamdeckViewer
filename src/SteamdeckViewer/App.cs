using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace SteamdeckViewer
{
	public class App : Application
	{
		public override void Initialize()
		{
			Styles.Add(new FluentTheme());
			RequestedThemeVariant = AppSettings.Current.ColorMode.ToThemeVariant();
		}

		public override void OnFrameworkInitializationCompleted()
		{
			if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
			{
				var window = new MainWindow();
				desktop.MainWindow = window;

				// Ошибка в обработчике кнопки не должна ронять приложение (например, посреди заливки):
				// пишем её в журнал и показываем, а работа продолжается
				Dispatcher.UIThread.UnhandledException += (_, e) =>
				{
					e.Handled = true;
					ErrorLog.Write(e.Exception);
					_ = Dialogs.Error(window, $"Непредвиденная ошибка: {e.Exception.Message}\n\nПодробности в журнале: {ErrorLog.FilePath}");
				};
			}

			TaskScheduler.UnobservedTaskException += (_, e) =>
			{
				ErrorLog.Write(e.Exception);
				e.SetObserved();
			};
			AppDomain.CurrentDomain.UnhandledException += (_, e) =>
			{
				if (e.ExceptionObject is Exception exception)
				{
					ErrorLog.Write(exception);
				}
			};

			base.OnFrameworkInitializationCompleted();
		}
	}
}

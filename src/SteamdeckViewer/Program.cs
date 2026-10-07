using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using SteamdeckViewer.Core;

namespace SteamdeckViewer
{
	internal static class Program
	{
		[STAThread]
		private static void Main(string[] args)
		{
			// Один экземпляр: настройки сохраняются целиком, и второе окно затёрло бы изменения первого
			using var single = new Mutex(true, @"Local\SteamdeckViewer.SingleInstance", out bool first);
			if (!first)
			{
				// Команду из Unity выполнит уже открытая программа
				ExternalCommand? command = ExternalCommand.Parse(args);
				if (command != null &&
				    !command.SendAsync(ExternalCommand.PipeName, TimeSpan.FromSeconds(10), CancellationToken.None).GetAwaiter().GetResult())
				{
					ShowError("CarX Deck Tools уже открыта, но не приняла команду из Unity. Закройте программу и повторите.");
				}

				ActivateRunningInstance();
				return;
			}

			AppSettings.RememberExecutablePath();
			BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
		}

		private static AppBuilder BuildAvaloniaApp()
		{
			return AppBuilder.Configure<App>()
				.UsePlatformDetect()
				.WithInterFont();
		}

		private static void ActivateRunningInstance()
		{
			if (!OperatingSystem.IsWindows())
			{
				return;
			}

			using var current = Process.GetCurrentProcess();
			foreach (Process process in Process.GetProcessesByName(current.ProcessName))
			{
				using (process)
				{
					if (process.Id != current.Id && process.MainWindowHandle != IntPtr.Zero)
					{
						ShowWindowAsync(process.MainWindowHandle, 9); // SW_RESTORE
						SetForegroundWindow(process.MainWindowHandle);
						return;
					}
				}
			}
		}

		// Окна Avalonia во втором экземпляре нет, поэтому системное сообщение
		private static void ShowError(string text)
		{
			if (OperatingSystem.IsWindows())
			{
				MessageBoxW(IntPtr.Zero, text, AppInfo.Name, 0x10); // MB_ICONERROR
			}
		}

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool ShowWindowAsync(IntPtr window, int command);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool SetForegroundWindow(IntPtr window);
	}
}

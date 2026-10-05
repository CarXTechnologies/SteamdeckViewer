using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;

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
				ActivateRunningInstance();
				return;
			}

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

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool ShowWindowAsync(IntPtr window, int command);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool SetForegroundWindow(IntPtr window);
	}
}

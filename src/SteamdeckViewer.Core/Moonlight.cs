using System.Diagnostics;

namespace SteamdeckViewer.Core;

public sealed record StreamOptions(int Width, int Height, int Fps, int BitrateKbps, bool Fullscreen, bool PerformanceOverlay);

// Moonlight на ПК — клиент стрима Sunshine. Встраивать видео в окно приложения не нужно:
// Moonlight уже умеет низкую задержку, HDR, геймпад и ввод, мы лишь запускаем его с нужными аргументами
public static class Moonlight
{
	// В режиме Desktop Sunshine просто снимает экран Deck — и Game Mode, и рабочий стол
	public const string DesktopApp = "Desktop";
	public const string DownloadUrl = "https://moonlight-stream.org/";

	public static string? FindExecutable(string? configured)
	{
		if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
		{
			return configured;
		}

		string[] roots =
		[
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
		];

		return roots
			.Where(r => !string.IsNullOrEmpty(r))
			.Select(r => Path.Combine(r, "Moonlight Game Streaming", "Moonlight.exe"))
			.FirstOrDefault(File.Exists);
	}

	public static IReadOnlyList<string> PairArguments(string host, string pin)
	{
		return ["pair", host, "--pin", pin];
	}

	// Абсолютная мышь — режим удалённого рабочего стола: курсор ПК совпадает с курсором на экране Deck
	public static IReadOnlyList<string> StreamArguments(string host, StreamOptions options)
	{
		var args = new List<string>
		{
			"stream", host, DesktopApp,
			"--display-mode", options.Fullscreen ? "fullscreen" : "windowed",
			"--resolution", $"{options.Width}x{options.Height}",
			"--fps", options.Fps.ToString(),
			"--bitrate", options.BitrateKbps.ToString(),
			"--absolute-mouse",
			"--no-quit-after"
		};

		if (options.PerformanceOverlay)
		{
			args.Add("--performance-overlay");
		}

		return args;
	}

	public static Process Start(string executable, IReadOnlyList<string> arguments)
	{
		var info = new ProcessStartInfo(executable) { UseShellExecute = false };
		foreach (string argument in arguments)
		{
			info.ArgumentList.Add(argument);
		}

		return Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить Moonlight.");
	}

	public static string NewPin()
	{
		return System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");
	}

	public static string ClientName()
	{
		return "SteamdeckViewer-" + SunshineHost.SafeName(Environment.MachineName);
	}
}

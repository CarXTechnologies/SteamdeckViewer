using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Styling;
using SteamdeckViewer.Core;

namespace SteamdeckViewer;

public enum AppColorMode
{
	System,
	Light,
	Dark
}

// Настройки и сохранённые устройства/профили: %APPDATA%\SteamdeckViewer\settings.json
public sealed class AppSettings
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		Converters =
		{
			new JsonStringEnumConverter()
		}
	};

	private static readonly string SettingsPath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamdeckViewer", "settings.json");

	private static AppSettings? s_current;

	public static AppSettings Current => s_current ??= Load();

	public AppColorMode ColorMode { get; set; } = AppColorMode.System;
	public List<DeckDevice> Devices { get; set; } = [];
	public int SelectedDevice { get; set; }
	public List<BuildProfile> Profiles { get; set; } = [];
	public int SelectedProfile { get; set; }
	public bool AutoConnect { get; set; } = true;
	public bool AutoRefreshStatus { get; set; } = true;
	public string? LastDownloadFolder { get; set; }

	// Удалённый экран
	public string? MoonlightPath { get; set; }
	public int StreamResolution { get; set; }
	public int StreamFps { get; set; } = 60;
	public int StreamBitrateMbps { get; set; } = 20;
	public bool StreamFullscreen { get; set; }
	public bool StreamPerformanceOverlay { get; set; }
	public bool ForceGamescopeComposite { get; set; } = true;

	// Папки обмена; пустой путь — «Steam Deck» на рабочем столе ПК
	public bool ExchangeEnabled { get; set; } = true;
	public string? ExchangeFolder { get; set; }

	// Путь к exe для меню Build/Steam Deck в Unity: так редактор находит программу без настройки
	public static void RememberExecutablePath()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
			File.WriteAllText(Path.Combine(Path.GetDirectoryName(SettingsPath)!, "app-path.txt"), Environment.ProcessPath);
		}
		catch
		{
			// ignore
		}
	}

	// Через временный файл: оборванная запись не оставит полупустой settings.json
	public void Save()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
			string temp = SettingsPath + ".tmp";
			File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
			File.Move(temp, SettingsPath, overwrite: true);
		}
		catch
		{
			// ignore
		}
	}

	private static AppSettings Load()
	{
		AppSettings settings;
		try
		{
			settings = File.Exists(SettingsPath)
				? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings()
				: new AppSettings();
		}
		catch
		{
			// Нечитаемый файл не затираем молча: откладываем копию, чтобы устройства и профили можно было вернуть
			try
			{
				File.Copy(SettingsPath, SettingsPath + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
			}
			catch
			{
				// ignore
			}

			settings = new AppSettings();
		}

		if (settings.Profiles.Count == 0)
		{
			settings.Profiles.Add(new BuildProfile());
		}

		return settings;
	}
}

public static class AppColorModeExtensions
{
	public static ThemeVariant ToThemeVariant(this AppColorMode mode)
	{
		return mode switch
		{
			AppColorMode.Light => ThemeVariant.Light,
			AppColorMode.Dark => ThemeVariant.Dark,
			_ => ThemeVariant.Default
		};
	}
}

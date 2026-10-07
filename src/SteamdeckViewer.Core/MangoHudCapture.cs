using System.Globalization;

namespace SteamdeckViewer.Core;

public sealed record MangoHudLog(string Name, string RemotePath, long Size);

// Итоги замера: FPS и время кадра по каждому кадру, плюс средние и пики по железу Deck
public sealed record MangoHudSummary(
	int Frames, double Seconds, double AverageFps, double Low1Fps, double Low01Fps, double AverageFrameTime, double MaxFrameTime,
	double? CpuLoad, double? GpuLoad, double? CpuTempMax, double? GpuTempMax, double? CpuPower, double? GpuPower, double? RamMax, double? VramMax)
{
	private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

	public string Describe()
	{
		var lines = new List<string>
		{
			$"Замер: {(int)Seconds / 60} мин {(int)Seconds % 60} с, кадров: {Frames.ToString("N0", Ru)}",
			$"FPS: средний {F(AverageFps)} · 1% low {F(Low1Fps)} · 0,1% low {F(Low01Fps)}",
			$"Время кадра: среднее {F(AverageFrameTime)} мс · максимум {F(MaxFrameTime)} мс",
			Hardware("CPU", CpuLoad, CpuTempMax, CpuPower),
			Hardware("GPU", GpuLoad, GpuTempMax, GpuPower)
		};

		if (RamMax != null || VramMax != null)
		{
			lines.Add("Память: " + string.Join(" · ", new[]
			{
				RamMax is { } ram ? $"RAM до {F(ram)} ГБ" : null,
				VramMax is { } vram ? $"VRAM до {F(vram)} ГБ" : null
			}.Where(s => s != null)));
		}

		return string.Join('\n', lines);
	}

	private static string Hardware(string name, double? load, double? tempMax, double? power)
	{
		var parts = new List<string>();
		if (load is { } l)
		{
			parts.Add($"загрузка {l.ToString("F0", Ru)} %");
		}

		if (tempMax is { } t)
		{
			parts.Add($"до {t.ToString("F0", Ru)} °C");
		}

		if (power is { } p)
		{
			parts.Add($"{F(p)} Вт в среднем");
		}

		return $"{name}: " + (parts.Count > 0 ? string.Join(", ", parts) : "нет данных");
	}

	private static string F(double value)
	{
		return value.ToString("F1", Ru);
	}
}

// MangoHud на Deck (входит в SteamOS): оверлей поверх игры с FPS, временем кадра, загрузкой, температурами и мощностью CPU/GPU
// и запись замеров в CSV на каждый кадр. Включается переменными окружения игры на один запуск; Development-сборка не нужна.
// Профайлер Unity показывает, что происходит внутри игры, а MangoHud — как ведёт себя железо Deck (троттлинг, лимит мощности)
public static class MangoHudCapture
{
	public const string Root = "~/.local/share/carx-deck-tools/mangohud";

	internal const string Hud =
		"fps,frametime,frame_timing,cpu_stats,gpu_stats,cpu_temp,gpu_temp,cpu_power,gpu_power,ram,vram,battery,position=top-left,font_size=18";

	public static string Folder(BuildProfile profile)
	{
		return Root + "/" + profile.GameId;
	}

	// null — MangoHud в профиле выключен. MANGOHUD_CONFIG важнее любых файлов настроек MangoHud, в том числе от оверлея Steam
	public static IReadOnlyDictionary<string, string>? Environment(BuildProfile profile, string absoluteFolder)
	{
		if (!profile.MangoHudOverlay && !profile.MangoHudLog)
		{
			return null;
		}

		var options = new List<string> { profile.MangoHudOverlay ? Hud : "no_display" };
		if (profile.MangoHudLog)
		{
			// Путь без запятых и пробелов: id игры из [A-Za-z0-9_.], домашняя папка deck
			options.Add("output_folder=" + absoluteFolder);
			options.Add("autostart_log=1");
			if (profile.MangoHudLogSeconds > 0)
			{
				options.Add("log_duration=" + profile.MangoHudLogSeconds.ToString(CultureInfo.InvariantCulture));
			}
		}

		return new Dictionary<string, string>
		{
			["MANGOHUD"] = "1",
			["MANGOHUD_CONFIG"] = string.Join(',', options)
		};
	}

	public static async Task<IReadOnlyDictionary<string, string>?> PrepareAsync(DeckConnection deck, BuildProfile profile, CancellationToken ct)
	{
		if (!profile.MangoHudOverlay && !profile.MangoHudLog)
		{
			return null;
		}

		if (profile.MangoHudLog)
		{
			CommandResult created = await deck.RunAsync("mkdir -p " + Sh.Path(Folder(profile)), ct);
			if (!created.Success)
			{
				throw new InvalidOperationException("Не удалось создать папку для замеров MangoHud: " + created.Combined.Trim());
			}
		}

		return Environment(profile, deck.ResolvePath(Folder(profile)));
	}

	public static async Task<MangoHudLog?> FindLatestAsync(DeckConnection deck, BuildProfile profile, CancellationToken ct)
	{
		Dictionary<string, (long Size, long MTime)> files = await FolderSync.ListRemoteAsync(deck, Folder(profile), ct);
		return Latest(files) is { } latest
			? new MangoHudLog(latest.Name, deck.ResolvePath(Folder(profile) + "/" + latest.Name), latest.Size)
			: null;
	}

	public static async Task DeleteAllAsync(DeckConnection deck, BuildProfile profile, CancellationToken ct)
	{
		await deck.RunAsync($"rm -f {Sh.Path(Folder(profile))}/*.csv", ct);
	}

	// Свой файл итогов MangoHud пишет только при штатной остановке записи, поэтому итоги считаются по основному CSV
	internal static (string Name, long Size)? Latest(IReadOnlyDictionary<string, (long Size, long MTime)> files)
	{
		return files
			.Where(f => !f.Key.Contains('/') && f.Key.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
			            !f.Key.EndsWith("_summary.csv", StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(f => f.Value.MTime)
			.ThenByDescending(f => f.Key, StringComparer.Ordinal)
			.Select(f => ((string, long)?)(f.Key, f.Value.Size))
			.FirstOrDefault();
	}

	// CSV MangoHud: сведения о системе, затем строка «fps,frametime,cpu_load,…,elapsed» и по строке на кадр
	public static MangoHudSummary? Summarize(string csv)
	{
		string[] lines = csv.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		int header = Array.FindIndex(lines, l => l.StartsWith("fps,", StringComparison.Ordinal) && l.Contains("frametime", StringComparison.Ordinal));
		if (header < 0)
		{
			return null;
		}

		string[] columns = lines[header].Split(',');
		int Column(string name) => Array.IndexOf(columns, name);
		int frametimeColumn = Column("frametime");

		var rows = new List<double[]>();
		foreach (string line in lines.Skip(header + 1))
		{
			string[] cells = line.Split(',');
			if (cells.Length != columns.Length)
			{
				continue;
			}

			var row = new double[cells.Length];
			for (int i = 0; i < cells.Length; i++)
			{
				row[i] = double.TryParse(cells[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : double.NaN;
			}

			if (row[frametimeColumn] > 0)
			{
				rows.Add(row);
			}
		}

		if (rows.Count == 0)
		{
			return null;
		}

		double[] frametimes = rows.Select(r => r[frametimeColumn]).Order().ToArray();
		double average = frametimes.Average();
		int elapsedColumn = Column("elapsed");
		double seconds = elapsedColumn >= 0 && rows.Count > 1
			? (rows[^1][elapsedColumn] - rows[0][elapsedColumn]) / 1e9
			: frametimes.Sum() / 1000;

		double? Mean(string name) => Values(name) is { Length: > 0 } values ? values.Average() : null;
		double? Max(string name) => Values(name) is { Length: > 0 } values ? values.Max() : null;
		double[]? Values(string name)
		{
			int column = Column(name);
			return column < 0 ? null : rows.Select(r => r[column]).Where(v => !double.IsNaN(v)).ToArray();
		}

		return new MangoHudSummary(
			rows.Count, Math.Max(seconds, 0), 1000 / average,
			1000 / Percentile(frametimes, 0.99), 1000 / Percentile(frametimes, 0.999),
			average, frametimes[^1],
			Mean("cpu_load"), Mean("gpu_load"), Max("cpu_temp"), Max("gpu_temp"), Mean("cpu_power"), Mean("gpu_power"),
			Max("ram_used"), Max("gpu_vram_used"));
	}

	// Значение, ниже которого лежит доля p отсортированных времён кадра: 99-й процентиль — это «1% low»
	private static double Percentile(double[] sorted, double p)
	{
		int index = (int)Math.Ceiling(p * sorted.Length) - 1;
		return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
	}
}

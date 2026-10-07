namespace SteamdeckViewer.Core;

public sealed record ProfilerRecording(string Name, string RemotePath, long Size);

// Запись Unity Profiler на Deck без редактора: плеер с -profiler-enable -profiler-log-file пишет .raw с первого кадра,
// включая загрузку, и сеть на замеры не влияет. Работает только в Development-сборках; файл открывается
// в Unity: Window → Analysis → Profiler → Load
public static class ProfilerCapture
{
	public const string Root = "~/.local/share/carx-deck-tools/profiler";

	// Буфер профайлера плеера по умолчанию 16 МБ; на старте игры его не хватает, и кадры теряются
	private const long MaxUsedMemory = 256L * 1024 * 1024;

	public static string Folder(BuildProfile profile)
	{
		return Root + "/" + profile.GameId;
	}

	public static string FileName(BuildProfile profile, DateTime now)
	{
		return $"{profile.GameId}_{now:yyyyMMdd_HHmmss}.raw";
	}

	// Путь абсолютный: в argv.json для запуска через Steam «~» никто не раскроет.
	// Пробелов в нём нет (id игры из [A-Za-z0-9_.], домашняя папка deck), поэтому без кавычек
	public static string Arguments(string absoluteFile, int frameCount)
	{
		return $"-profiler-enable -profiler-log-file {absoluteFile} -profiler-maxusedmemory {MaxUsedMemory}" +
		       (frameCount > 0 ? $" -profiler-capture-frame-count {frameCount}" : string.Empty);
	}

	// Создаёт папку записей и возвращает аргументы плеера и путь будущего файла
	public static async Task<(string Arguments, string RemotePath)> PrepareAsync(DeckConnection deck, BuildProfile profile, DateTime now, CancellationToken ct)
	{
		CommandResult created = await deck.RunAsync("mkdir -p " + Sh.Path(Folder(profile)), ct);
		if (!created.Success)
		{
			throw new InvalidOperationException("Не удалось создать папку для записи профайлера: " + created.Combined.Trim());
		}

		string path = deck.ResolvePath(Folder(profile) + "/" + FileName(profile, now));
		return (Arguments(path, profile.ProfilerFrameCount), path);
	}

	public static async Task<ProfilerRecording?> FindLatestAsync(DeckConnection deck, BuildProfile profile, CancellationToken ct)
	{
		Dictionary<string, (long Size, long MTime)> files = await FolderSync.ListRemoteAsync(deck, Folder(profile), ct);
		return Latest(files) is { } latest
			? new ProfilerRecording(latest.Name, deck.ResolvePath(Folder(profile) + "/" + latest.Name), latest.Size)
			: null;
	}

	public static async Task DeleteAllAsync(DeckConnection deck, BuildProfile profile, CancellationToken ct)
	{
		await deck.RunAsync($"rm -f {Sh.Path(Folder(profile))}/*.raw", ct);
	}

	internal static (string Name, long Size)? Latest(IReadOnlyDictionary<string, (long Size, long MTime)> files)
	{
		return files
			.Where(f => !f.Key.Contains('/') && f.Key.EndsWith(".raw", StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(f => f.Value.MTime)
			.ThenByDescending(f => f.Key, StringComparer.Ordinal)
			.Select(f => ((string, long)?)(f.Key, f.Value.Size))
			.FirstOrDefault();
	}
}

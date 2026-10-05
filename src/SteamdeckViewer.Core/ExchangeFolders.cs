using System.Text;
using System.Text.Json;

namespace SteamdeckViewer.Core;

public sealed record ExchangeFile(string RelativePath, long Size, long MTime);

// Итог одного прохода: что ушло в каждую сторону и что не получилось (повторится на следующем проходе)
public sealed record ExchangeResult(IReadOnlyList<string> ToPc, IReadOnlyList<string> ToDeck, IReadOnlyList<string> Errors)
{
	public bool IsEmpty => ToPc.Count == 0 && ToDeck.Count == 0 && Errors.Count == 0;
}

// Папки обмена: «На ПК» и «С ПК» на рабочем столе Deck, «С Deck» и «На Deck» в папке на ПК. В стриме файл перетаскивается
// в «На ПК» и через пару секунд появляется на ПК, и наоборот. Передача односторонняя и без удалений: стёртый в одной
// папке файл в другой остаётся, изменённый — передаётся заново и перезаписывает копию.
// Файл уходит, только когда размер и время изменения не менялись между двумя проходами: так не уйдёт недокопированный
public sealed class ExchangeFolders
{
	public const string DeckOutbox = "На ПК";
	public const string DeckInbox = "С ПК";
	public const string PcInbox = "С Deck";
	public const string PcOutbox = "На Deck";

	private const string PartSuffix = ".sdv-part";

	// Служебные и недописанные файлы файловых менеджеров и программ: на другой стороне они не нужны
	internal static readonly FileFilter Junk =
		new(".directory *.part *.kate-swp *~ .~lock.* desktop.ini Thumbs.db .DS_Store ~$* *.tmp *.crdownload *" + PartSuffix);

	// xdg-user-dir отдаёт $HOME, если папка рабочего стола не задана
	internal static readonly string PrepareScript = $$"""
		d=$(xdg-user-dir DESKTOP 2>/dev/null)
		if [ -z "$d" ] || [ "$d" = "$HOME" ]; then d="$HOME/Desktop"; fi
		mkdir -p "$d/{{DeckOutbox}}" "$d/{{DeckInbox}}" && printf '%s\n' "$d"
		""";

	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private readonly string m_manifestPath;
	private readonly Manifest m_manifest;
	private Dictionary<string, ExchangeFile>? m_lastDeck;
	private Dictionary<string, ExchangeFile>? m_lastPc;

	public ExchangeFolders(string pcRoot, string manifestPath)
	{
		PcRoot = pcRoot;
		m_manifestPath = manifestPath;
		m_manifest = LoadManifest(manifestPath);
	}

	public string PcRoot { get; }
	public string PcInboxPath => Path.Combine(PcRoot, PcInbox);
	public string PcOutboxPath => Path.Combine(PcRoot, PcOutbox);
	public string? DeckDesktop { get; private set; }

	// Что уже передано: путь → размер и время изменения источника. Хранится на ПК для каждого Deck отдельно,
	// чтобы после перезапуска приложения не передавать заново то, что уже удалили на другой стороне
	public static string DefaultManifestPath(DeckDevice device)
	{
		string key = string.IsNullOrEmpty(device.HostKeyFingerprint) ? $"{device.User}@{device.Host}" : device.HostKeyFingerprint;
		var name = new StringBuilder();
		foreach (char c in key)
		{
			name.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '@' ? c : '_');
		}

		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamdeckViewer", "exchange", name + ".json");
	}

	public static string DefaultPcRoot()
	{
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Steam Deck");
	}

	public async Task PrepareAsync(DeckConnection deck, CancellationToken ct)
	{
		Directory.CreateDirectory(PcInboxPath);
		Directory.CreateDirectory(PcOutboxPath);

		CommandResult result = await deck.RunAsync(PrepareScript, ct);
		string desktop = result.Output.Trim();
		if (!result.Success || !desktop.StartsWith('/'))
		{
			throw new InvalidOperationException("Не удалось создать папки обмена на рабочем столе Deck: " + result.Combined.Trim());
		}

		DeckDesktop = desktop;
	}

	public async Task<ExchangeResult> RunOnceAsync(DeckConnection deck, CancellationToken ct)
	{
		if (DeckDesktop == null)
		{
			await PrepareAsync(deck, ct);
		}

		var errors = new List<string>();
		List<string> toPc = await PullFromDeckAsync(deck, errors, ct);
		List<string> toDeck = await PushToDeckAsync(deck, errors, ct);
		if (toPc.Count > 0 || toDeck.Count > 0)
		{
			SaveManifest();
		}

		return new ExchangeResult(toPc, toDeck, errors);
	}

	private async Task<List<string>> PullFromDeckAsync(DeckConnection deck, List<string> errors, CancellationToken ct)
	{
		string outbox = Sh.JoinPath(DeckDesktop!, DeckOutbox);
		CommandResult listing = await deck.RunAsync(FolderSync.ListCommand(outbox), ct);
		if (!listing.Success)
		{
			throw new InvalidOperationException("Не удалось прочитать папку «" + DeckOutbox + "» на Deck: " + listing.Combined.Trim());
		}

		Dictionary<string, ExchangeFile> current = FolderSync.ParseListing(listing.Output)
			.Where(e => !Junk.IsExcluded(e.Key))
			.ToDictionary(e => e.Key, e => new ExchangeFile(e.Key, e.Value.Size, e.Value.MTime), StringComparer.Ordinal);
		List<ExchangeFile> ready = Ready(current, m_lastDeck, m_manifest.ToPc);
		m_lastDeck = current;

		var done = new List<string>();
		if (ready.Count == 0)
		{
			return done;
		}

		var sftp = deck.GetSftp();
		foreach (ExchangeFile file in ready)
		{
			ct.ThrowIfCancellationRequested();
			string target = Path.Combine(PcInboxPath, ToWindowsPath(file.RelativePath));
			string temp = target + PartSuffix;
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				using (FileStream output = File.Create(temp))
				{
					sftp.DownloadFile(Sh.JoinPath(outbox, file.RelativePath), output);
				}

				File.SetLastWriteTimeUtc(temp, DateTimeOffset.FromUnixTimeSeconds(file.MTime).UtcDateTime);
				File.Move(temp, target, overwrite: true);
				m_manifest.ToPc[file.RelativePath] = file;
				done.Add(file.RelativePath);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or Renci.SshNet.Common.SshException)
			{
				TryDelete(temp);
				errors.Add($"{file.RelativePath}: {e.Message}");
			}
		}

		return done;
	}

	private async Task<List<string>> PushToDeckAsync(DeckConnection deck, List<string> errors, CancellationToken ct)
	{
		// Папку на ПК могли удалить или переименовать — возвращаем, как и папки на Deck
		Directory.CreateDirectory(PcOutboxPath);
		Dictionary<string, ExchangeFile> current = FolderSync.ListLocal(PcOutboxPath, Junk)
			.ToDictionary(f => f.RelativePath, f => new ExchangeFile(f.RelativePath, f.Size, f.MTime), StringComparer.Ordinal);
		List<ExchangeFile> ready = Ready(current, m_lastPc, m_manifest.ToDeck);
		m_lastPc = current;

		// Файл, который ещё пишет другая программа (копирование в Проводнике), пропускаем до следующего прохода
		var files = new List<LocalFile>();
		foreach (ExchangeFile file in ready)
		{
			string path = Path.Combine(PcOutboxPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
			if (CanRead(path))
			{
				files.Add(new LocalFile(path, file.RelativePath, file.Size, file.MTime));
			}
		}

		if (files.Count == 0)
		{
			return [];
		}

		try
		{
			await FolderSync.UploadAsync(deck, Sh.JoinPath(DeckDesktop!, DeckInbox), files, FolderSync.GuessMode, null, ct);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			errors.Add(e.Message);
			return [];
		}

		foreach (LocalFile file in files)
		{
			m_manifest.ToDeck[file.RelativePath] = new ExchangeFile(file.RelativePath, file.Size, file.MTime);
		}

		return files.Select(f => f.RelativePath).ToList();
	}

	// Готов к передаче: не менялся с прошлого прохода и в таком виде ещё не передавался
	internal static List<ExchangeFile> Ready(IReadOnlyDictionary<string, ExchangeFile> current, IReadOnlyDictionary<string, ExchangeFile>? previous,
		IReadOnlyDictionary<string, ExchangeFile> sent)
	{
		if (previous == null)
		{
			return [];
		}

		return current.Values
			.Where(f => previous.TryGetValue(f.RelativePath, out ExchangeFile? before) && before == f &&
			            !(sent.TryGetValue(f.RelativePath, out ExchangeFile? done) && done == f))
			.OrderBy(f => f.RelativePath, StringComparer.Ordinal)
			.ToList();
	}

	// Имена Linux, недопустимые в Windows: запрещённые символы, точки и пробелы в конце, зарезервированные имена устройств
	internal static string ToWindowsPath(string relativePath)
	{
		char[] invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '|', '?', '*', '\\']).ToArray();
		IEnumerable<string> segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(segment =>
		{
			var name = new StringBuilder(segment.Length);
			foreach (char c in segment)
			{
				name.Append(c < 32 || invalid.Contains(c) ? '_' : c);
			}

			string result = name.ToString().TrimEnd('.', ' ');
			if (result.Length == 0)
			{
				result = "_";
			}

			string stem = result.Split('.')[0].ToUpperInvariant();
			bool reserved = stem is "CON" or "PRN" or "AUX" or "NUL" ||
			                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
			                char.IsAsciiDigit(stem[3]);
			return reserved ? "_" + result : result;
		});

		return string.Join(Path.DirectorySeparatorChar, segments);
	}

	private static bool CanRead(string path)
	{
		try
		{
			using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			return true;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch
		{
			// ignore
		}
	}

	private static Manifest LoadManifest(string path)
	{
		try
		{
			return File.Exists(path) ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonOptions) ?? new Manifest() : new Manifest();
		}
		catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
		{
			return new Manifest();
		}
	}

	private void SaveManifest()
	{
		Directory.CreateDirectory(Path.GetDirectoryName(m_manifestPath)!);
		string temp = m_manifestPath + ".tmp";
		File.WriteAllText(temp, JsonSerializer.Serialize(m_manifest, JsonOptions));
		File.Move(temp, m_manifestPath, overwrite: true);
	}

	internal sealed class Manifest
	{
		public Dictionary<string, ExchangeFile> ToPc { get; set; } = new(StringComparer.Ordinal);
		public Dictionary<string, ExchangeFile> ToDeck { get; set; } = new(StringComparer.Ordinal);
	}
}

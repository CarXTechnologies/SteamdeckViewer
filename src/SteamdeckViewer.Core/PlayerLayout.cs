using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SteamdeckViewer.Core;

public sealed record PlayerLayoutReport(int Checked, IReadOnlyList<string> Problems);

// Проверка целостности CarX Street до запуска. Игра (BootLayout) сверяет каждый бинарник своей папки с индексом
// player_layout.bundle: лишний, недостающий или изменённый файл — ошибка E29 (на экране E3239-000) без строки в Player.log.
// Здесь та же сверка файлов на Deck с индексом из сборки на ПК, чтобы назвать виновника заранее.
// Подпись индекса не проверяется: ключа у программы нет, а индекс берётся из только что собранной сборки
public sealed partial class PlayerLayout
{
	public const string IndexName = "player_layout.bundle";

	private const uint Tag = 0x31594C50;
	private const int Revision = 2;
	private const int DigestSize = 32;

	private static readonly string[] MappedExtensions = [".dll", ".exe", ".so", ".dylib", ".x86_64"];

	// Путь → длина и SHA-256; без учёта регистра, как словарь в игре
	private readonly Dictionary<string, (long Length, string Digest)> m_entries;

	// Защищённый бандл EOS тоже в индексе и единственный там .bundle; игра узнаёт его по имени в любой папке
	private readonly string? m_protectedBundle;

	private PlayerLayout(string indexPath, string indexDigest, Dictionary<string, (long Length, string Digest)> entries)
	{
		IndexPath = indexPath;
		IndexDigest = indexDigest;
		m_entries = entries;
		m_protectedBundle = entries.Keys
			.Select(FileName)
			.FirstOrDefault(name => Extension(name).Equals(".bundle", StringComparison.OrdinalIgnoreCase));
	}

	// Относительно папки сборки, через «/»
	public string IndexPath { get; }
	public string IndexDigest { get; }
	public int Count => m_entries.Count;

	// Папка, в которой игра ищет индекс: <Игра>_Data/StreamingAssets/aa/
	private string AddressablesFolder
	{
		get
		{
			const string marker = "/StreamingAssets/aa/";
			int at = IndexPath.IndexOf(marker, StringComparison.Ordinal);
			return at >= 0 ? IndexPath[..(at + marker.Length)] : IndexPath[..(IndexPath.LastIndexOf('/') + 1)];
		}
	}

	// Индекс в сборке на ПК: <Игра>_Data/StreamingAssets/aa/**/player_layout.bundle. null — сборка без проверки целостности
	public static PlayerLayout? Load(string buildFolder)
	{
		if (!Directory.Exists(buildFolder))
		{
			return null;
		}

		var found = new List<string>();
		foreach (string data in Directory.EnumerateDirectories(buildFolder, "*_Data"))
		{
			string addressables = Path.Combine(data, "StreamingAssets", "aa");
			if (Directory.Exists(addressables))
			{
				found.AddRange(Directory.EnumerateFiles(addressables, IndexName, SearchOption.AllDirectories));
			}
		}

		if (found.Count == 0)
		{
			return null;
		}

		if (found.Count > 1)
		{
			throw new InvalidDataException($"В сборке на ПК несколько {IndexName}: игра не пройдёт проверку целостности.");
		}

		return Parse(File.ReadAllBytes(found[0]), Path.GetRelativePath(buildFolder, found[0]).Replace('\\', '/'));
	}

	// Формат BuildProcessSaveLayout: длина и данные, длина и подпись; в данных метка, версия, CRC бандла и записи
	internal static PlayerLayout Parse(byte[] index, string indexPath)
	{
		try
		{
			using var reader = new BinaryReader(new MemoryStream(index, false), Encoding.UTF8);
			int payloadSize = reader.ReadInt32();
			if (payloadSize <= 0 || payloadSize > 8 * 1024 * 1024)
			{
				throw new InvalidDataException("неверная длина данных");
			}

			byte[] payload = reader.ReadBytes(payloadSize);
			int signatureSize = reader.ReadInt32();
			if (payload.Length != payloadSize || signatureSize < 128 || signatureSize > 1024 ||
			    reader.ReadBytes(signatureSize).Length != signatureSize || reader.BaseStream.Position != index.Length)
			{
				throw new InvalidDataException("неверная длина подписи");
			}

			using var entries = new BinaryReader(new MemoryStream(payload, false), Encoding.UTF8);
			if (entries.ReadUInt32() != Tag || entries.ReadInt32() != Revision)
			{
				throw new InvalidDataException("неизвестная версия");
			}

			entries.ReadUInt32(); // CRC защищённого бандла
			int count = entries.ReadInt32();
			if (count <= 0 || count > 32768)
			{
				throw new InvalidDataException("неверное число записей");
			}

			var result = new Dictionary<string, (long, string)>(count, StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < count; i++)
			{
				string key = entries.ReadString().Replace('\\', '/').TrimStart('/');
				long length = entries.ReadInt64();
				byte[] digest = entries.ReadBytes(DigestSize);
				if (key.Length == 0 || length < 0 || digest.Length != DigestSize || !result.TryAdd(key, (length, Convert.ToHexStringLower(digest))))
				{
					throw new InvalidDataException("неверная запись " + key);
				}
			}

			if (entries.BaseStream.Position != payload.Length)
			{
				throw new InvalidDataException("лишние данные в конце");
			}

			return new PlayerLayout(indexPath, Convert.ToHexStringLower(SHA256.HashData(index)), result);
		}
		catch (EndOfStreamException)
		{
			throw new InvalidDataException($"{indexPath} на ПК не читается: файл обрезан.");
		}
		catch (InvalidDataException e) when (!e.Message.Contains(indexPath, StringComparison.Ordinal))
		{
			throw new InvalidDataException($"{indexPath} на ПК не читается: {e.Message}.");
		}
	}

	public async Task<PlayerLayoutReport> CheckDeckAsync(DeckConnection deck, string remoteFolder, CancellationToken ct)
	{
		Dictionary<string, (long Size, long MTime)> files = await FolderSync.ListRemoteAsync(deck, remoteFolder, ct);

		List<string> mapped = files.Keys.Where(IsMappedByName).ToList();
		List<string> unnamed = files.Keys.Where(p => !IsMappedByName(p) && NeedsHeader(p)).ToList();
		if (unnamed.Count > 0)
		{
			mapped.AddRange(await ReadExecutablesAsync(deck, remoteFolder, unnamed, ct));
		}

		var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
		List<string> toHash = mapped.Append(IndexPath).Where(files.ContainsKey).Distinct().ToList();
		await FolderSync.HashRemoteAsync(deck, remoteFolder, toHash, (path, hash) =>
		{
			lock (hashes)
			{
				hashes[path] = hash;
			}
		}, ct);

		return new PlayerLayoutReport(mapped.Count, Compare(files, mapped, hashes));
	}

	// Как PlayerLayoutFilter.IsMappedFile, кроме файлов без расширения: их игра отбирает по первым байтам
	internal bool IsMappedByName(string path)
	{
		string name = FileName(path);
		return MappedExtensions.Contains(Extension(name), StringComparer.OrdinalIgnoreCase) ||
		       VersionedSharedObject().IsMatch(name) ||
		       (m_protectedBundle != null && name.Equals(m_protectedBundle, StringComparison.OrdinalIgnoreCase));
	}

	internal static bool NeedsHeader(string path)
	{
		return Extension(FileName(path)).Length == 0;
	}

	// Первые 4 байта в hex: «#!», ELF или Mach-O — как HasUnixExecutableHeader в игре
	internal static bool IsExecutableHeader(string hex)
	{
		if (hex.Length != 8 || hex.AsSpan().ContainsAnyExcept("0123456789abcdefABCDEF"))
		{
			return false;
		}

		byte[] header = Convert.FromHexString(hex);
		if (header[0] == '#' && header[1] == '!')
		{
			return true;
		}

		return BitConverter.ToUInt32(header) is 0x464C457F or 0xFEEDFACE or 0xCEFAEDFE or 0xFEEDFACF or 0xCFFAEDFE
			or 0xCAFEBABE or 0xBEBAFECA or 0xCAFEBABF or 0xBFBAFECA;
	}

	// Те же шаги, что BootLayout: ровно один индекс, каждый проверяемый файл есть в индексе с тем же размером и хешем,
	// и все записи индекса нашлись
	internal List<string> Compare(IReadOnlyDictionary<string, (long Size, long MTime)> deckFiles, IReadOnlyCollection<string> mapped,
		IReadOnlyDictionary<string, string> hashes)
	{
		var problems = new List<string>();

		int indexes = deckFiles.Keys.Count(p => p.StartsWith(AddressablesFolder, StringComparison.Ordinal) && FileName(p) == IndexName);
		if (indexes == 0)
		{
			problems.Add($"нет на Deck: {IndexPath}");
		}
		else if (indexes > 1)
		{
			problems.Add($"несколько {IndexName} в {AddressablesFolder}");
		}
		else if (!hashes.TryGetValue(IndexPath, out string? indexHash) || indexHash != IndexDigest)
		{
			problems.Add($"{IndexPath} на Deck не тот, что в сборке на ПК");
		}

		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string path in mapped.Order(StringComparer.Ordinal))
		{
			if (!m_entries.TryGetValue(path, out (long Length, string Digest) entry))
			{
				problems.Add("лишний файл: " + path);
				continue;
			}

			seen.Add(path);
			if (deckFiles[path].Size != entry.Length)
			{
				problems.Add("другой размер: " + path);
			}
			else if (!hashes.TryGetValue(path, out string? hash))
			{
				problems.Add("не прочитался на Deck: " + path);
			}
			else if (hash != entry.Digest)
			{
				problems.Add("другое содержимое: " + path);
			}
		}

		problems.AddRange(m_entries.Keys.Where(path => !seen.Contains(path)).Order(StringComparer.Ordinal).Select(path => "нет на Deck: " + path));
		return problems;
	}

	// На входе пути через \0; на выходе «hex первых 4 байт<TAB>путь» — короткий файл даст меньше 8 цифр
	internal static string HeaderCommand(string remoteFolder)
	{
		const string probe = "printf '%s\\t%s\\n' \"$(head -c 4 -- \"$1\" | od -An -tx1 | tr -d ' \\n')\" \"$1\"";
		return $"cd {Sh.Path(remoteFolder)} && xargs -0 -r -n 1 sh -c {Sh.Quote(probe)} sh";
	}

	internal static IEnumerable<string> ParseExecutables(string output)
	{
		foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			int tab = line.IndexOf('\t');
			if (tab >= 0 && IsExecutableHeader(line[..tab]))
			{
				yield return line[(tab + 1)..];
			}
		}
	}

	private static async Task<IEnumerable<string>> ReadExecutablesAsync(DeckConnection deck, string remoteFolder, IReadOnlyList<string> paths, CancellationToken ct)
	{
		byte[] list = Encoding.UTF8.GetBytes(string.Join('\0', paths) + "\0");
		CommandResult result = await deck.RunWithInputAsync(HeaderCommand(remoteFolder), async (input, token) => await input.WriteAsync(list, token), ct);
		return ParseExecutables(result.Output).ToList();
	}

	private static string FileName(string path)
	{
		return path[(path.LastIndexOf('/') + 1)..];
	}

	// Как Path.GetExtension: от последней точки имени; точка в конце имени — расширения нет
	private static string Extension(string name)
	{
		int dot = name.LastIndexOf('.');
		return dot >= 0 && dot < name.Length - 1 ? name[dot..] : string.Empty;
	}

	[GeneratedRegex(@"\.so(\.\d+)+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex VersionedSharedObject();
}

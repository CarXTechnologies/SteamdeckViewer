using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.Text;

namespace SteamdeckViewer.Core;

public sealed record SyncProgress(string Stage, long DoneBytes, long TotalBytes, int DoneFiles, int TotalFiles);

public sealed record SyncSummary(int Uploaded, int Unchanged, int Deleted, long Bytes, TimeSpan Elapsed);

public sealed record LocalFile(string FullPath, string RelativePath, long Size, long MTime);

// Заливка папок на Deck. Вместо тысяч мелких SFTP-операций файлы идут одним tar-потоком
// в stdin `tar -x` на Deck, а при повторной заливке уходят только файлы с другим размером или временем изменения.
// Байты не меняются: игра сверяет их с подписанным индексом player_layout.bundle
public static class FolderSync
{
	// Как --chmod=Du=rwx,Dgo=rx,Fu=rwx,Fog=rx у Valve: NTFS не хранит бит исполнения, поэтому он нужен всем файлам билда
	public const UnixFileMode ExecutableMode =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
		UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
		UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

	public const UnixFileMode RegularMode =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

	public static async Task<SyncSummary> SyncAsync(DeckConnection deck, string localFolder, string remoteFolder,
		FileFilter filter, bool deleteExtraneous, IProgress<SyncProgress>? progress, CancellationToken ct)
	{
		var stopwatch = Stopwatch.StartNew();
		progress?.Report(new SyncProgress("Сравнение файлов", 0, 0, 0, 0));

		Dictionary<string, (long Size, long MTime)> remote = await ListRemoteAsync(deck, remoteFolder, ct);
		List<LocalFile> local = ListLocal(localFolder, filter);

		var changed = new List<LocalFile>();
		foreach (LocalFile file in local)
		{
			if (!remote.TryGetValue(file.RelativePath, out (long Size, long MTime) existing) ||
			    existing.Size != file.Size ||
			    existing.MTime != file.MTime)
			{
				changed.Add(file);
			}
		}

		long bytes = changed.Sum(f => f.Size);
		if (changed.Count > 0)
		{
			await UploadAsync(deck, remoteFolder, changed, _ => ExecutableMode, progress, ct);
		}

		int deleted = 0;
		if (deleteExtraneous)
		{
			var localPaths = new HashSet<string>(local.Select(f => f.RelativePath), StringComparer.Ordinal);
			List<string> extra = remote.Keys.Where(p => !localPaths.Contains(p) && !filter.IsExcluded(p)).ToList();
			if (extra.Count > 0)
			{
				progress?.Report(new SyncProgress("Удаление лишних файлов", bytes, bytes, changed.Count, changed.Count));
				await DeleteRemoteAsync(deck, remoteFolder, extra, ct);
				deleted = extra.Count;
			}
		}

		return new SyncSummary(changed.Count, local.Count - changed.Count, deleted, bytes, stopwatch.Elapsed);
	}

	// Бит исполнения из Windows не приходит: ставим его скриптам и бинарникам Linux-сборок, остальным 644
	public static UnixFileMode GuessMode(LocalFile file)
	{
		string extension = Path.GetExtension(file.RelativePath).ToLowerInvariant();
		return extension is "" or ".sh" or ".x86_64" or ".appimage" or ".so" || extension.StartsWith(".so.", StringComparison.Ordinal)
			? ExecutableMode
			: RegularMode;
	}

	// Время изменения сравнивается с точностью до секунды: столько хранит tar
	public static List<LocalFile> ListLocal(string localFolder, FileFilter? filter)
	{
		string root = Path.GetFullPath(localFolder);
		var result = new List<LocalFile>();
		foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
		{
			string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
			if (filter != null && filter.IsExcluded(relative))
			{
				continue;
			}

			var info = new FileInfo(path);
			result.Add(new LocalFile(path, relative, info.Length, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds()));
		}

		return result;
	}

	public static async Task UploadAsync(DeckConnection deck, string remoteFolder, IReadOnlyList<LocalFile> files,
		Func<LocalFile, UnixFileMode> modeFor, IProgress<SyncProgress>? progress, CancellationToken ct)
	{
		long total = files.Sum(f => f.Size);
		long done = 0;
		int doneFiles = 0;
		var throttle = Stopwatch.StartNew();

		void Report(bool force)
		{
			if (force || throttle.ElapsedMilliseconds >= 150)
			{
				throttle.Restart();
				progress?.Report(new SyncProgress("Передача файлов", done, total, doneFiles, files.Count));
			}
		}

		CommandResult result = await deck.RunWithInputAsync(ExtractCommand(remoteFolder), (input, token) =>
			WriteTarAsync(input, files, modeFor, read =>
			{
				done += read;
				Report(false);
			}, () =>
			{
				doneFiles++;
				Report(false);
			}, token), ct);

		Report(true);
		if (!result.Success)
		{
			throw new InvalidOperationException("tar на Deck завершился с ошибкой: " + result.Combined.Trim());
		}
	}

	// PAX-архив с временем изменения в целых секундах и заданными правами; GNU tar на Deck распаковывает его как есть
	public static async Task WriteTarAsync(Stream output, IReadOnlyList<LocalFile> files, Func<LocalFile, UnixFileMode> modeFor,
		Action<int>? onBytes, Action? onFile, CancellationToken ct)
	{
		await using var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
		foreach (LocalFile file in files)
		{
			ct.ThrowIfCancellationRequested();

			await using var source = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
			await using var counted = new CountingReadStream(source, read => onBytes?.Invoke(read));

			var entry = new PaxTarEntry(TarEntryType.RegularFile, file.RelativePath)
			{
				ModificationTime = DateTimeOffset.FromUnixTimeSeconds(file.MTime),
				Mode = modeFor(file),
				DataStream = counted
			};

			await writer.WriteEntryAsync(entry, ct);
			onFile?.Invoke();
		}
	}

	internal static string ExtractCommand(string remoteFolder)
	{
		string folder = Sh.Path(remoteFolder);
		return $"mkdir -p {folder} && cd {folder} && " +
		       Sh.KeepAwake("заливка файлов", "tar -x -f - --no-same-owner --warning=no-timestamp");
	}

	internal static string ListCommand(string remoteFolder)
	{
		string folder = Sh.Path(remoteFolder);
		return $"mkdir -p {folder} && cd {folder} && find . -type f -printf '%P\\t%s\\t%T@\\n'";
	}

	internal static string DeleteCommand(string remoteFolder)
	{
		return $"cd {Sh.Path(remoteFolder)} && xargs -0 -r rm -f -- && find . -mindepth 1 -type d -empty -delete";
	}

	private static async Task<Dictionary<string, (long Size, long MTime)>> ListRemoteAsync(DeckConnection deck, string remoteFolder, CancellationToken ct)
	{
		CommandResult listing = await deck.RunAsync(ListCommand(remoteFolder), ct);
		if (!listing.Success)
		{
			throw new InvalidOperationException("Не удалось прочитать папку на Deck: " + listing.Combined.Trim());
		}

		return ParseListing(listing.Output);
	}

	internal static Dictionary<string, (long Size, long MTime)> ParseListing(string output)
	{
		var result = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
		foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			string[] parts = line.Split('\t');
			if (parts.Length == 3 &&
			    long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long size) &&
			    double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double mtime))
			{
				result[parts[0]] = (size, (long)Math.Floor(mtime));
			}
		}

		return result;
	}

	private static async Task DeleteRemoteAsync(DeckConnection deck, string remoteFolder, IReadOnlyList<string> relativePaths, CancellationToken ct)
	{
		byte[] list = Encoding.UTF8.GetBytes(string.Join('\0', relativePaths) + "\0");

		CommandResult result = await deck.RunWithInputAsync(DeleteCommand(remoteFolder),
			async (input, token) => await input.WriteAsync(list, token), ct);

		if (!result.Success)
		{
			throw new InvalidOperationException("Не удалось удалить лишние файлы на Deck: " + result.Combined.Trim());
		}
	}

	// TarWriter берёт длину записи из DataStream, поэтому обёртка обязана оставаться seekable
	private sealed class CountingReadStream(Stream inner, Action<int> onRead) : Stream
	{
		public override bool CanRead => true;
		public override bool CanSeek => inner.CanSeek;
		public override bool CanWrite => false;
		public override long Length => inner.Length;

		public override long Position
		{
			get => inner.Position;
			set => inner.Position = value;
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			int read = inner.Read(buffer, offset, count);
			onRead(read);
			return read;
		}

		public override int Read(Span<byte> buffer)
		{
			int read = inner.Read(buffer);
			onRead(read);
			return read;
		}

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			int read = await inner.ReadAsync(buffer, cancellationToken);
			onRead(read);
			return read;
		}

		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
		{
			return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
		}

		public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
		public override void Flush() { }
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}
}

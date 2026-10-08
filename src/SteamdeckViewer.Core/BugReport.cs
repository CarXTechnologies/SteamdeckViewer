using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace SteamdeckViewer.Core;

// Что известно на момент отчёта; из этого собирается report.txt
public sealed record BugReportData(
	DateTime Time,
	string ToolVersion,
	string PcName,
	BuildProfile Profile,
	DateTime? LocalBuildTime,
	DeckStatus? Status,
	string Address,
	IReadOnlyDictionary<string, string> Extra,
	string? PlayerLog,
	string? MangoHudName,
	MangoHudSummary? MangoHud,
	IReadOnlyList<string> Files,
	IReadOnlyList<string> Missing);

// Отчёт для задачи одним архивом: Player.log и Player-prev.log, скриншот, последний замер MangoHud
// и report.txt — версии, состояние Deck и ошибки из лога. Что собрать не удалось, перечислено в report.txt
public static class BugReport
{
	private const int MaxErrors = 20;

	// Строки Player.log, по которым видно, что за сборка: версия Unity и (у CarX Street) версия игры
	private static readonly Regex[] VersionLines =
	[
		new(@"^Initialize engine version:", RegexOptions.CultureInvariant),
		new(@"\bVersion:.*\bbuild", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)
	];

	public static string FileName(BuildProfile profile, DateTime now)
	{
		return $"{profile.GameId}_{now:yyyy-MM-dd_HH-mm-ss}.zip";
	}

	// Версия клиента Steam, время и размер залитой сборки, сколько процессов запущено из её папки
	internal static string InfoScript(BuildProfile profile)
	{
		string folder = Sh.Path(profile.RemoteFolder);
		string exe = Sh.Quote(profile.Executable.Trim().Replace('\\', '/'));
		return $$"""
			echo "steam_client=$(grep -h '"version"' ~/.steam/steam/package/steam_client_*.manifest 2>/dev/null | head -n1 | tr -dc '0-9')"
			dir=$(cd {{folder}} 2>/dev/null && pwd -P) || exit 0
			echo "build_time=$(stat -c %Y "$dir"/{{exe}} 2>/dev/null)"
			echo "build_size=$(du -sb "$dir" 2>/dev/null | cut -f1)"
			n=0
			for p in /proc/[0-9]*; do
				case "$(readlink "$p/exe" 2>/dev/null)" in "$dir"/*) n=$((n+1));; esac
			done
			echo "game_processes=$n"
			""";
	}

	public static async Task<IReadOnlyList<string>> CreateAsync(DeckConnection deck, BuildProfile profile, string zipPath, string toolVersion, DateTime now, CancellationToken ct)
	{
		string dir = Directory.CreateTempSubdirectory("carx-deck-report-").FullName;
		var files = new List<string>();
		var missing = new List<string>();
		try
		{
			string log = deck.ResolvePath(profile.PlayerLogPath);
			string? playerLog = null;
			foreach ((string remote, string name) in new[] { (log, "Player.log"), (Sh.ParentPath(log) + "/Player-prev.log", "Player-prev.log") })
			{
				if (await TryDownloadAsync(deck, remote, Path.Combine(dir, name)))
				{
					files.Add(name);
					playerLog ??= await File.ReadAllTextAsync(Path.Combine(dir, name), ct);
				}
				else
				{
					missing.Add($"{name}: нет на Deck ({remote})");
				}
			}

			try
			{
				await DeckScreenshot.TakeAsync(deck, Path.Combine(dir, "screenshot.png"), ct);
				files.Add("screenshot.png");
			}
			catch (Exception e) when (e is not OperationCanceledException)
			{
				missing.Add("скриншот: " + e.Message);
			}

			string? mangoHudName = null;
			MangoHudSummary? mangoHud = null;
			if (await MangoHudCapture.FindLatestAsync(deck, profile, ct) is { } latest)
			{
				Directory.CreateDirectory(Path.Combine(dir, "mangohud"));
				string local = Path.Combine(dir, "mangohud", latest.Name);
				if (await TryDownloadAsync(deck, latest.RemotePath, local))
				{
					mangoHudName = latest.Name;
					mangoHud = MangoHudCapture.Summarize(await File.ReadAllTextAsync(local, ct));
					files.Add("mangohud/" + latest.Name);
				}
			}

			DeckStatus status = await DeckStatus.QueryAsync(deck, ct);
			DeckStatus extra = DeckStatus.Parse((await deck.RunAsync(InfoScript(profile), ct)).Output);

			string localExe = Path.Combine(profile.LocalFolder, profile.Executable);
			var data = new BugReportData(now, toolVersion, Environment.MachineName, profile,
				File.Exists(localExe) ? File.GetLastWriteTime(localExe) : null,
				status, deck.Device.Host, extra.Values, playerLog, mangoHudName, mangoHud, files, missing);
			await File.WriteAllTextAsync(Path.Combine(dir, "report.txt"), Describe(data), new UTF8Encoding(true), ct);

			File.Delete(zipPath);
			ZipFile.CreateFromDirectory(dir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
			return missing;
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	internal static string Describe(BugReportData d)
	{
		var text = new StringBuilder();
		text.AppendLine($"Отчёт CarX Deck Tools {d.ToolVersion}, {d.Time:yyyy-MM-dd HH:mm:ss}, ПК {d.PcName}");

		text.AppendLine().AppendLine("== Сборка");
		BuildProfile p = d.Profile;
		text.AppendLine($"Профиль: {p.Name} (id {p.GameId})");
		text.AppendLine($"На ПК: {(p.LocalFolder.Length > 0 ? p.LocalFolder : "—")}" +
		                (d.LocalBuildTime is { } local ? $", {p.Executable} от {local:yyyy-MM-dd HH:mm}" : string.Empty));
		string deckTime = long.TryParse(Value(d.Extra, "build_time"), out long unix)
			? $", {p.Executable} от {DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime:yyyy-MM-dd HH:mm}"
			: ", сборки на Deck нет";
		string deckSize = long.TryParse(Value(d.Extra, "build_size"), out long size) ? $", {DeckStatus.FormatBytes(size)}" : string.Empty;
		text.AppendLine($"На Deck: {p.RemoteFolder}{deckTime}{deckSize}");
		text.AppendLine($"Запуск: {p.LaunchMode}, {p.Runtime}; аргументы: {(p.Arguments.Length > 0 ? p.Arguments : "—")}; окружение: {(p.EnvironmentVariables.Length > 0 ? p.EnvironmentVariables : "—")}");
		text.AppendLine("Игра сейчас: " + (int.TryParse(Value(d.Extra, "game_processes"), out int processes) && processes > 0 ? "запущена" : "не запущена"));

		var model = new PlayerLogModel(int.MaxValue);
		if (d.PlayerLog != null)
		{
			foreach (string line in d.PlayerLog.Split('\n'))
			{
				model.Add(line);
			}

			foreach (Regex pattern in VersionLines)
			{
				if (model.Lines.FirstOrDefault(l => pattern.IsMatch(l.Text)) is { } version)
				{
					text.AppendLine("Player.log: " + version.Text.Trim());
				}
			}
		}

		text.AppendLine().AppendLine(d.PlayerLog == null
			? "== Player.log не скачан"
			: $"== Player.log: ошибок {model.Count(LogLevel.Error)}, предупреждений {model.Count(LogLevel.Warning)}");
		List<string> errors = ErrorHeadlines(model).ToList();
		foreach (string error in errors.Take(MaxErrors))
		{
			text.AppendLine("  " + error);
		}

		if (errors.Count > MaxErrors)
		{
			text.AppendLine($"  … и ещё {errors.Count - MaxErrors}");
		}

		text.AppendLine().AppendLine("== Deck");
		if (d.Status is { } status)
		{
			foreach ((string key, string label) in DeckStatus.Rows)
			{
				string value = status.Display(key, d.Address);
				if (key == "steam" && Value(d.Extra, "steam_client") is { Length: > 0 } client)
				{
					value += $", клиент {client}";
				}

				text.AppendLine($"{label}: {value}");
			}
		}
		else
		{
			text.AppendLine("состояние не получено");
		}

		text.AppendLine().AppendLine("== MangoHud");
		text.AppendLine(d.MangoHudName == null
			? "замеров нет"
			: $"последний замер: {d.MangoHudName}\n{d.MangoHud?.Describe() ?? "в замере нет кадров"}");

		text.AppendLine().AppendLine("== Файлы");
		text.AppendLine(d.Files.Count > 0 ? string.Join(", ", d.Files) : "—");
		foreach (string missing in d.Missing)
		{
			text.AppendLine("не собрано — " + missing);
		}

		return text.ToString();
	}

	// Строка, по которой узнаётся ошибка: заголовок исключения или строка с признаком ошибки, иначе последняя строка
	// сообщения перед стеком. Первая строка записи не годится: над сообщением бывают нативные строки движка (PlayerLogModel)
	internal static IEnumerable<string> ErrorHeadlines(PlayerLogModel model)
	{
		foreach (IGrouping<LogEntry?, LogLine> entry in model.Lines.Where(l => l.Level == LogLevel.Error && !l.IsTool).GroupBy(l => l.Entry))
		{
			List<LogLine> lines = entry.ToList();
			LogLine line = lines.FirstOrDefault(l => !l.IsFrame && l.OwnLevel == LogLevel.Error)
			               ?? lines.TakeWhile(l => !l.IsFrame).LastOrDefault()
			               ?? lines[0];
			yield return line.Text.Trim();
		}
	}

	private static string Value(IReadOnlyDictionary<string, string> values, string key)
	{
		return values.TryGetValue(key, out string? value) ? value : string.Empty;
	}

	private static async Task<bool> TryDownloadAsync(DeckConnection deck, string remotePath, string localPath)
	{
		return await Task.Run(() =>
		{
			Renci.SshNet.SftpClient sftp = deck.GetSftp();
			if (!sftp.Exists(remotePath))
			{
				return false;
			}

			using FileStream output = File.Create(localPath);
			sftp.DownloadFile(remotePath, output);
			return true;
		});
	}
}

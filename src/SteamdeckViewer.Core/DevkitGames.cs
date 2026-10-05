using System.Text.Json;

namespace SteamdeckViewer.Core;

// Тестовые билды в формате SteamOS Devkit: файлы в ~/devkit-game/<id>, рядом <id>-argv.json, -settings.json, -env.json.
// Steam сам показывает такие игры в библиотеке как «Devkit Game: <id>» и запускает их по команде devkit-1,
// которую принимает через ~/.steam/steam.pipe с токеном сессии (тот же протокол, что у devkit-utils Valve)
public static class DevkitGames
{
	public const string GamesRoot = "~/devkit-game";

	public static async Task RegisterShortcutAsync(DeckConnection deck, BuildProfile profile, CancellationToken ct)
	{
		CommandResult written = await deck.RunAsync(WriteLaunchFilesScript(profile), ct);
		if (!written.Success)
		{
			throw new InvalidOperationException("Не удалось записать настройки запуска: " + written.Combined.Trim());
		}

		await SendSteamCommandAsync(deck, "create-shortcut", "gameid=" + profile.GameId, ct);
	}

	public static async Task WriteSteamAppIdAsync(DeckConnection deck, BuildProfile profile, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(profile.SteamAppId))
		{
			return;
		}

		await deck.RunAsync($"printf '%s\\n' {Sh.Quote(profile.SteamAppId.Trim())} > {Sh.Path(profile.RemoteFolder)}/steam_appid.txt", ct);
	}

	public static async Task LaunchAsync(DeckConnection deck, BuildProfile profile, CancellationToken ct)
	{
		if (profile.LaunchMode == LaunchMode.Steam)
		{
			await SendSteamCommandAsync(deck, "run-game/", "gameid=" + profile.GameId, ct);
			return;
		}

		if (profile.Runtime == DeckRuntime.Proton)
		{
			throw new InvalidOperationException("Windows-билд под Proton запускается только через Steam.");
		}

		CommandResult result = await deck.RunAsync(DirectLaunchScript(profile), ct);
		if (!result.Success)
		{
			throw new InvalidOperationException(result.ExitCode == 2
				? "Билд ещё не залит на Deck."
				: "Не удалось запустить: " + result.Combined.Trim());
		}
	}

	public static async Task<int> StopAsync(DeckConnection deck, BuildProfile profile, CancellationToken ct)
	{
		CommandResult result = await deck.RunAsync(StopScript(profile), ct);
		return int.TryParse(result.Output.Trim(), out int count) ? count : 0;
	}

	public static async Task DeleteAsync(DeckConnection deck, BuildProfile profile, CancellationToken ct)
	{
		await StopAsync(deck, profile, ct);

		string root = Sh.Path(GamesRoot);
		string id = profile.GameId;
		await deck.RunAsync(
			$"rm -rf {Sh.Path(profile.RemoteFolder)} {root}/{Sh.Quote(id + "-argv.json")} {root}/{Sh.Quote(id + "-settings.json")} {root}/{Sh.Quote(id + "-env.json")}",
			ct);

		try
		{
			await SendSteamCommandAsync(deck, "delete-shortcut", "gameid=" + id, ct);
		}
		catch (InvalidOperationException)
		{
			// Steam не запущен — ярлык уберётся сам: без папки игры Steam его больше не покажет
		}
	}

	// ---------------------------------------------------------------- скрипты для Deck

	// Настройки запуска в формате devkit-utils Valve: argv — одна строка «команда + аргументы» относительно папки игры
	internal static string WriteLaunchFilesScript(BuildProfile profile)
	{
		string id = profile.GameId;
		string root = Sh.Path(GamesRoot);

		var settings = new Dictionary<string, string>();
		if (profile.Runtime == DeckRuntime.Proton)
		{
			settings["steam_play"] = "1";
			settings["steam_play_debug"] = "0";
			settings["compat_tool"] = "proton-stable";
		}
		else
		{
			settings["steam_play"] = "0";
			settings["compat_tool"] = profile.Runtime == DeckRuntime.SteamLinuxRuntime3 ? "SteamLinuxRuntime_sniper" : string.Empty;
		}

		string argv = JsonSerializer.Serialize(new[] { profile.StartCommand });
		string settingsJson = JsonSerializer.Serialize(settings);
		IReadOnlyDictionary<string, string> env = profile.ParseEnvironment();

		return
			$"mkdir -p {root} && " +
			$"printf '%s' {Sh.Quote(argv)} > {root}/{Sh.Quote(id + "-argv.json")} && " +
			$"printf '%s' {Sh.Quote(settingsJson)} > {root}/{Sh.Quote(id + "-settings.json")} && " +
			(env.Count > 0
				? $"printf '%s' {Sh.Quote(JsonSerializer.Serialize(env))} > {root}/{Sh.Quote(id + "-env.json")}"
				: $"rm -f {root}/{Sh.Quote(id + "-env.json")}");
	}

	// Окружение графической сессии берём из менеджера systemd пользователя, DISPLAY — только если его там нет
	internal static string DirectLaunchScript(BuildProfile profile)
	{
		string setenv = string.Join(' ', profile.ParseEnvironment().Select(kv => "--setenv=" + Sh.Quote(kv.Key + "=" + kv.Value)));
		string unit = Sh.Quote(UnitName(profile));
		return
			$"cd {Sh.Path(profile.RemoteFolder)} || exit 2\n" +
			$"systemctl --user stop {unit} 2>/dev/null; systemctl --user reset-failed {unit} 2>/dev/null\n" +
			"display=''\n" +
			"systemctl --user show-environment 2>/dev/null | grep -q '^DISPLAY=' || display='--setenv=DISPLAY=:0'\n" +
			$"systemd-run --user --unit={unit} --collect --working-directory=\"$PWD\" $display {setenv} /bin/sh -c {Sh.Quote("exec " + profile.StartCommand)}";
	}

	// Ищет процессы, чей исполняемый файл или рабочая папка внутри папки билда; так ловятся и игры под Proton
	internal static string StopScript(BuildProfile profile)
	{
		return
			$"dir=$(cd {Sh.Path(profile.RemoteFolder)} 2>/dev/null && pwd -P) || {{ echo 0; exit 0; }}\n" +
			$"systemctl --user stop {Sh.Quote(UnitName(profile))} 2>/dev/null\n" +
			"pids=''\n" +
			"for p in /proc/[0-9]*; do\n" +
			"  pid=${p#/proc/}; [ \"$pid\" = \"$$\" ] && continue\n" +
			"  e=$(readlink \"$p/exe\" 2>/dev/null); c=$(readlink \"$p/cwd\" 2>/dev/null)\n" +
			"  case \"$e\" in \"$dir\"/*) pids=\"$pids $pid\"; continue;; esac\n" +
			"  case \"$c\" in \"$dir\"|\"$dir\"/*) pids=\"$pids $pid\";; esac\n" +
			"done\n" +
			"[ -z \"$pids\" ] && { echo 0; exit 0; }\n" +
			"kill $pids 2>/dev/null; sleep 2\n" +
			"for p in $pids; do kill -0 \"$p\" 2>/dev/null && kill -9 \"$p\" 2>/dev/null; done\n" +
			"echo $pids | wc -w";
	}

	// Ответ Steam пишет в файл: сначала <path>.lock, затем <path> или <path>.error, затем удаляет .lock
	internal static string SteamCommandScript(string command, string query, string responseDir)
	{
		string response = responseDir + "/response";
		string url = $"{command}?response={Uri.EscapeDataString(response)}&{query}";
		string dir = Sh.Quote(responseDir);
		string resp = Sh.Quote(response);

		return
			"p=$(cat ~/.steam/steam.pid 2>/dev/null)\n" +
			"if [ -z \"$p\" ] || ! kill -0 \"$p\" 2>/dev/null; then exit 3; fi\n" +
			"token=$(cat ~/.steam/steam.token 2>/dev/null) || exit 6\n" +
			// Без этой проверки перенаправление ниже создало бы обычный файл вместо канала Steam
			"[ -p ~/.steam/steam.pipe ] || exit 7\n" +
			$"mkdir -p {dir}\n" +
			// Открытие на чтение и запись (как 'wb+' у Valve), чтобы не повиснуть, если Steam не читает канал
			$"printf 'devkit-1 steam://devkit-1/%s/%s\\n' \"$token\" {Sh.Quote(url)} 1<> ~/.steam/steam.pipe || exit 7\n" +
			"i=0\n" +
			"while [ $i -lt 20 ]; do\n" +
			"  sleep 1; i=$((i+1))\n" +
			$"  if [ -f {resp}.error ]; then cat {resp}.error; rm -rf {dir}; exit 4; fi\n" +
			$"  if [ -f {resp} ] && [ ! -f {resp}.lock ]; then cat {resp}; rm -rf {dir}; exit 0; fi\n" +
			"done\n" +
			$"rm -rf {dir}; exit 5";
	}

	private static async Task<string> SendSteamCommandAsync(DeckConnection deck, string command, string query, CancellationToken ct)
	{
		string responseDir = "/tmp/sdv-" + Guid.NewGuid().ToString("N");
		CommandResult result = await deck.RunAsync(SteamCommandScript(command, query, responseDir), ct);
		return result.ExitCode switch
		{
			0 => result.Output.Trim(),
			3 => throw new InvalidOperationException("Steam на Deck не запущен."),
			4 => throw new InvalidOperationException("Steam вернул ошибку: " + result.Output.Trim()),
			5 => throw new InvalidOperationException("Steam не ответил за 20 секунд. Включён ли Developer Mode на Deck?"),
			6 => throw new InvalidOperationException("Нет ~/.steam/steam.token — Steam не готов принимать команды."),
			7 => throw new InvalidOperationException("Нет канала ~/.steam/steam.pipe — Steam не готов принимать команды."),
			_ => throw new InvalidOperationException($"Команда Steam завершилась с кодом {result.ExitCode}: {result.Combined.Trim()}")
		};
	}

	private static string UnitName(BuildProfile profile)
	{
		return "sdv-" + profile.GameId;
	}
}

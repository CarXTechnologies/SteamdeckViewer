using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SteamdeckViewer.Core;

namespace SteamdeckViewer.Tests;

public sealed class ShellQuoteTests
{
	[WslFact]
	public void QuoteRoundTrips()
	{
		string[] values = ["simple", "with space", "it's", "a\"b", "$HOME `id` \\ ; | & * ?", "кириллица", ""];
		string script = string.Join('\n', values.Select(v => $"printf '%s\\n' {Sh.Quote(v)}"));

		CommandResult result = Wsl.Run(script);

		Assert.Equal(values, result.Output.Split('\n')[..values.Length]);
	}

	[WslFact]
	public void PathExpandsHome()
	{
		string home = Wsl.NewHome();
		CommandResult result = Wsl.Run($"printf '%s|%s' {Sh.Path("~/a b/it's")} {Sh.Path("~")}", home);
		Assert.Equal($"{home}/a b/it's|{home}", result.Output);
	}
}

public sealed class FolderSyncLinuxTests
{
	[WslFact]
	public async Task TarStreamExtractsByteExactWithModesAndTimes()
	{
		string local = Directory.CreateTempSubdirectory("sdv-tar-").FullName;
		string tarPath = Path.Combine(local, "..", Path.GetFileName(local) + ".tar");
		try
		{
			byte[] big = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
			Write(local, "CarX_Street.x86_64", big);
			Write(local, "CarX_Street_Data/Managed/Assembly-CSharp.dll", "dll"u8.ToArray());
			Write(local, "Папка с пробелом/файл 'кавычки'.txt", Encoding.UTF8.GetBytes("привет"));
			Write(local, new string('d', 60) + "/" + new string('f', 80) + ".bin", [1, 2, 3]);
			Write(local, "CarX_Street_BurstDebugInformation_DoNotShip/x.txt", [9]);
			Write(local, "empty.txt", []);

			List<LocalFile> files = FolderSync.ListLocal(local, new FileFilter(new BuildProfile().Excludes));
			Assert.DoesNotContain(files, f => f.RelativePath.Contains("DoNotShip"));
			Assert.Equal(5, files.Count);

			await using (FileStream tar = File.Create(tarPath))
			{
				await FolderSync.WriteTarAsync(tar, files, _ => FolderSync.ExecutableMode, null, null, CancellationToken.None);
			}

			string home = Wsl.NewHome();
			const string remote = "~/devkit-game/CarX-Street";
			CommandResult extract = Wsl.Run($"cat {Sh.Quote(Wsl.ToLinuxPath(tarPath))} | ( {FolderSync.ExtractCommand(remote)} )", home);
			Assert.True(extract.Success, extract.Combined);

			// Листинг на «Deck» совпадает с локальным: повторная заливка ничего не отправит
			CommandResult listing = Wsl.Run(FolderSync.ListCommand(remote), home);
			Dictionary<string, (long Size, long MTime)> remoteFiles = FolderSync.ParseListing(listing.Output);
			Assert.Equal(files.Count, remoteFiles.Count);
			foreach (LocalFile file in files)
			{
				Assert.True(remoteFiles.TryGetValue(file.RelativePath, out (long Size, long MTime) r), file.RelativePath);
				Assert.Equal(file.Size, r.Size);
				Assert.Equal(file.MTime, r.MTime);
			}

			CommandResult modes = Wsl.Run($"cd {Sh.Path(remote)} && find . -type f -printf '%m\\n' | sort -u", home);
			Assert.Equal("755", modes.Output.Trim());

			CommandResult hash = Wsl.Run($"sha256sum {Sh.Path(remote + "/CarX_Street.x86_64")} | cut -d' ' -f1", home);
			Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(big)), hash.Output.Trim());

			Wsl.Run("rm -rf \"$HOME\"", home);
		}
		finally
		{
			Directory.Delete(local, recursive: true);
			File.Delete(tarPath);
		}
	}

	private static void Write(string root, string relativePath, byte[] content)
	{
		string path = Path.Combine(root, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, content);
	}

	[WslFact]
	public void DeleteCommandRemovesListedFilesAndEmptyFolders()
	{
		string home = Wsl.NewHome();
		const string remote = "~/game";
		string setup = $"mkdir -p {Sh.Path(remote)}/b {Sh.Path(remote)}/e && cd {Sh.Path(remote)} && touch a b/c b/d e/f 'sp ace'";
		string delete = $"printf 'a\\0b/c\\0e/f\\0sp ace\\0' | ( {FolderSync.DeleteCommand(remote)} )";

		CommandResult result = Wsl.Run($"{setup}\n{delete}\ncd {Sh.Path(remote)} && find . -mindepth 1 | sort", home);

		Assert.Equal("./b\n./b/d", result.Output.Trim());
	}

	// Файлы без расширения игра проверяет, только если они начинаются как исполняемые (ELF, #!, Mach-O)
	[WslFact]
	public void HeaderProbeFindsExecutablesWithoutExtension()
	{
		string home = Wsl.NewHome();
		const string remote = "~/game";
		string setup =
			$"mkdir -p {Sh.Path(remote)}/'sub dir' && cd {Sh.Path(remote)} && " +
			"printf '\\177ELF\\002\\001' > elf && printf '#!/bin/sh\\n' > 'sub dir/run me' && printf 'UnityFS' > level0 && printf 'ab' > short";
		string probe = $"printf 'elf\\0sub dir/run me\\0level0\\0short\\0missing\\0' | ( {PlayerLayout.HeaderCommand(remote)} )";

		CommandResult result = Wsl.Run($"{setup}\n{probe}", home);

		Assert.Equal(["elf", "sub dir/run me"], PlayerLayout.ParseExecutables(result.Output).Order(StringComparer.Ordinal));
		Assert.Contains("556e6974\tlevel0", result.Output);
		Assert.Contains("6162\tshort", result.Output);
	}

	// Пересобранный билд: размер тот же, время новое. Хеши с «Deck» совпадают с посчитанными на ПК,
	// а после touch листинг совпадает и по времени — следующая заливка обойдётся без хешей
	[WslFact]
	public void HashAndTouchCommandsLetRebuiltFilesStay()
	{
		string home = Wsl.NewHome();
		const string remote = "~/game";
		var files = new Dictionary<string, string>
		{
			["a.bin"] = "one",
			["sub dir/b 'q'.so"] = "two",
			["-dash"] = "three",
			["back\\slash"] = "four"
		};
		string list = home + "/list";
		string setup =
			$"mkdir -p {Sh.Path(remote)}/'sub dir' && cd {Sh.Path(remote)} && " +
			string.Join(" && ", files.Select(f => $"printf '%s' {Sh.Quote(f.Value)} > ./{Sh.Quote(f.Key)}")) +
			$" && printf '%s\\0' {string.Join(' ', files.Keys.Select(Sh.Quote))} missing > {Sh.Quote(list)}";

		CommandResult hashed = Wsl.Run($"{setup}\n{FolderSync.HashCommand(remote, list)}\n[ -e {Sh.Quote(list)} ] && echo LEFT", home);

		var parsed = new Dictionary<string, string>();
		foreach (string line in hashed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			Assert.NotEqual("LEFT", line);
			if (FolderSync.TryParseHashLine(line, out string path, out string hash))
			{
				parsed[path] = hash;
			}
		}

		// Имя с обратной косой sha256sum экранирует: такой файл не сверяется и зальётся заново
		Assert.Equal(["-dash", "a.bin", "sub dir/b 'q'.so"], parsed.Keys.Order(StringComparer.Ordinal));
		foreach ((string path, string hash) in parsed)
		{
			Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(files[path]))), hash);
		}

		string touch = $"printf '@1700000000\\0./a.bin\\0@1700000100\\0./-dash\\0@1700000200\\0./nope\\0' | ( {FolderSync.TouchCommand(remote)} )";
		CommandResult listing = Wsl.Run($"{touch}\n{FolderSync.ListCommand(remote)}", home);
		Dictionary<string, (long Size, long MTime)> remoteFiles = FolderSync.ParseListing(listing.Output);

		Assert.Equal(1700000000, remoteFiles["a.bin"].MTime);
		Assert.Equal(1700000100, remoteFiles["-dash"].MTime);
		Assert.False(remoteFiles.ContainsKey("nope"));
		Assert.Equal(4, remoteFiles.Count);
	}

	// Заливка под systemd-inhibit: блокировка сна, если её разрешает polkit, иначе только простоя, иначе без блокировки.
	// Данные из stdin доходят до команды в любом случае
	[WslFact]
	public void KeepAwakePicksAllowedLockAndPassesStdin()
	{
		const string fakeInhibit = """
			#!/bin/bash
			echo "inhibit $*" >> "$HOME/calls"
			[ -n "$DENY_ALL" ] && exit 1
			[ "$1" = "--what=sleep:idle" ] && [ -z "$ALLOW_SLEEP" ] && exit 1
			while [ $# -gt 0 ] && [ "$1" != "--mode=block" ] && [ "$1" != "true" ]; do shift; done
			[ "$1" = "--mode=block" ] && shift
			exec "$@"
			""";

		string run = Sh.KeepAwake("SteamdeckViewer: тест", "cat > out.txt");
		string script =
			$"mkdir -p ~/bin\ncat > ~/bin/systemd-inhibit <<'EOF'\n{fakeInhibit}\nEOF\nchmod +x ~/bin/systemd-inhibit\nexport PATH=\"$HOME/bin:$PATH\"\n" +
			$"printf 'idle' | ( {run} ); echo \"A=$(cat out.txt) $(grep -c 'block cat' ~/calls) $(grep 'block cat' ~/calls | cut -d' ' -f2)\"; rm ~/calls\n" +
			$"printf 'sleep' | ( export ALLOW_SLEEP=1; {run} ); echo \"B=$(cat out.txt) $(grep 'block cat' ~/calls | cut -d' ' -f2)\"; rm ~/calls\n" +
			$"printf 'none' | ( export DENY_ALL=1; {run} ); echo \"C=$(cat out.txt) $(grep -c 'block cat' ~/calls)\"";

		CommandResult result = Wsl.Run(script, Wsl.NewHome());

		Assert.Contains("A=idle 1 --what=idle", result.Output);
		Assert.Contains("B=sleep --what=sleep:idle", result.Output);
		Assert.Contains("C=none 0", result.Output);
	}
}

public sealed class DevkitGamesLinuxTests
{
	private static readonly BuildProfile Profile = new() { Name = "CarX Street", Arguments = "-screen-fullscreen 1" };

	// Steam, который читает ~/.steam/steam.pipe и отвечает файлом, как настоящий клиент при Developer Mode
	private const string FakeSteam = """
		mkdir -p ~/.steam
		sleep 300 & echo $! > ~/.steam/steam.pid
		printf 'TOKEN123' > ~/.steam/steam.token
		mkfifo ~/.steam/steam.pipe
		(
		  read -r line < ~/.steam/steam.pipe
		  printf '%s' "$line" > ~/received.txt
		  enc=$(printf '%s' "$line" | sed -E 's/.*response=([^&]*).*/\1/')
		  resp=$(python3 -c 'import sys, urllib.parse; print(urllib.parse.unquote(sys.argv[1]))' "$enc")
		  touch "$resp.lock"
		  printf '%s' "$REPLY_TEXT" > "$resp$REPLY_SUFFIX"
		  rm -f "$resp.lock"
		) &
		sleep 0.5
		""";

	[WslFact]
	public void SteamCommandGetsResponse()
	{
		string home = Wsl.NewHome();
		string script = "export REPLY_TEXT='registered' REPLY_SUFFIX=''\n" + FakeSteam +
		                Wsl.Subshell(DevkitGames.SteamCommandScript("create-shortcut", "gameid=" + Profile.GameId, "/tmp/sdv-resp1-" + Guid.NewGuid().ToString("N"))) +
		                "\necho \"|EXIT=$?\"; cat ~/received.txt; kill $(cat ~/.steam/steam.pid)";

		CommandResult result = Wsl.Run(script, home);

		Assert.True(result.Output.Contains("registered|EXIT=0"), result.Combined);
		Assert.Matches(@"devkit-1 steam://devkit-1/TOKEN123/create-shortcut\?response=%2Ftmp%2Fsdv-resp1-[0-9a-f]+%2Fresponse&gameid=CarX_Street", result.Output);
	}

	[WslFact]
	public void SteamCommandReportsError()
	{
		string home = Wsl.NewHome();
		string script = "export REPLY_TEXT='bad gameid' REPLY_SUFFIX='.error'\n" + FakeSteam +
		                Wsl.Subshell(DevkitGames.SteamCommandScript("run-game/", "gameid=x", "/tmp/sdv-resp2-" + Guid.NewGuid().ToString("N"))) +
		                "\necho \"|EXIT=$?\"; kill $(cat ~/.steam/steam.pid)";

		CommandResult result = Wsl.Run(script, home);

		Assert.True(result.Output.Contains("bad gameid|EXIT=4"), result.Combined);
	}

	[WslFact]
	public void SteamCommandWithoutSteam()
	{
		string home = Wsl.NewHome();
		CommandResult noSteam = Wsl.Run(Wsl.Subshell(DevkitGames.SteamCommandScript("run-game/", "gameid=x", "/tmp/sdv-x")) + "\necho \"EXIT=$?\"", home);
		Assert.Contains("EXIT=3", noSteam.Output);

		// Steam жив, но канала нет: скрипт не должен создать обычный файл на его месте
		string script = "mkdir -p ~/.steam; sleep 30 & echo $! > ~/.steam/steam.pid; printf T > ~/.steam/steam.token\n" +
		                Wsl.Subshell(DevkitGames.SteamCommandScript("run-game/", "gameid=x", "/tmp/sdv-y")) +
		                "\necho \"EXIT=$?\"; [ -e ~/.steam/steam.pipe ] && echo CREATED; kill $(cat ~/.steam/steam.pid)";
		CommandResult noPipe = Wsl.Run(script, Wsl.NewHome());
		Assert.Contains("EXIT=7", noPipe.Output);
		Assert.DoesNotContain("CREATED", noPipe.Output);
	}

	// Билд, залитый под прежним id с дефисом, переименовывается, а не заливается заново; уже существующую папку не затирает
	[WslFact]
	public void MigratesBuildUploadedUnderLegacyId()
	{
		string migrate = DevkitGames.MigrateLegacyFolderScript(Profile);
		string script =
			"mkdir -p ~/devkit-game/CarX-Street && echo big > ~/devkit-game/CarX-Street/data\n" +
			"touch ~/devkit-game/CarX-Street-argv.json ~/devkit-game/CarX-Street-settings.json\n" +
			$"{migrate}; echo \"EXIT=$?\"; ls ~/devkit-game; cat ~/devkit-game/CarX_Street/data\n" +
			"mkdir -p ~/devkit-game/CarX-Street && echo stale > ~/devkit-game/CarX-Street/data\n" +
			$"{migrate}; echo ---; cat ~/devkit-game/CarX_Street/data";

		CommandResult result = Wsl.Run(script, Wsl.NewHome());

		string[] parts = result.Output.Split("---\n");
		Assert.Contains("EXIT=0", parts[0]);
		Assert.Equal("EXIT=0\nCarX_Street\nbig\n", parts[0]);
		Assert.Equal("big\n", parts[1]);
	}

	[WslFact]
	public void WritesDevkitLaunchFiles()
	{
		string home = Wsl.NewHome();
		CommandResult result = Wsl.Run(DevkitGames.WriteLaunchFilesScript(Profile) +
		                               "\necho; cat ~/devkit-game/CarX_Street-argv.json; echo; cat ~/devkit-game/CarX_Street-settings.json; echo; cat ~/devkit-game/CarX_Street-env.json", home);
		Assert.True(result.Success, result.Combined);

		string[] lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal(["CarX_Street.x86_64 -screen-fullscreen 1"], JsonSerializer.Deserialize<string[]>(lines[0])!);

		var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(lines[1])!;
		Assert.Equal("0", settings["steam_play"]);
		Assert.Equal(string.Empty, settings["compat_tool"]);

		Assert.Equal("1", JsonSerializer.Deserialize<Dictionary<string, string>>(lines[2])!["SteamDeck"]);
	}

	// Запуск с записью профайлера: аргументы на один запуск дописываются к аргументам профиля в argv.json
	[WslFact]
	public void LaunchFilesCarryOneOffArguments()
	{
		string extra = ProfilerCapture.Arguments("/home/deck/.local/share/carx-deck-tools/profiler/CarX_Street/a.raw", 300);
		CommandResult result = Wsl.Run(DevkitGames.WriteLaunchFilesScript(Profile, new LaunchExtras(extra, null)) + "\necho; cat ~/devkit-game/CarX_Street-argv.json", Wsl.NewHome());
		Assert.True(result.Success, result.Combined);

		string argv = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
		Assert.Equal(["CarX_Street.x86_64 -screen-fullscreen 1 " + extra], JsonSerializer.Deserialize<string[]>(argv)!);
	}

	// MangoHud на один запуск через Steam: env.json остаётся с переменными профиля (Steam вычищает MANGOHUD* из окружения),
	// а переменные ставит обёртка в папке игры; Steam запускает её с теми же аргументами, она — игру
	[WslFact]
	public void LaunchWrapperCarriesMangoHudEnvironment()
	{
		var profile = new BuildProfile { Name = "CarX Street", Arguments = "-screen-fullscreen 1", MangoHudOverlay = true, MangoHudLog = true, MangoHudLogSeconds = 60 };
		IReadOnlyDictionary<string, string> environment = MangoHudCapture.Environment(profile, "/home/deck/m")!;
		const string fakeGame = "#!/bin/sh\necho \"MANGOHUD=$MANGOHUD\"; echo \"CONFIG=$MANGOHUD_CONFIG\"; for a in \"$@\"; do echo \"ARG=$a\"; done";
		string script =
			$"mkdir -p ~/devkit-game/CarX_Street && printf '%s\\n' {Sh.Quote(fakeGame)} > ~/devkit-game/CarX_Street/CarX_Street.x86_64 && chmod +x ~/devkit-game/CarX_Street/CarX_Street.x86_64\n" +
			DevkitGames.WriteLaunchFilesScript(profile, new LaunchExtras("-profiler-enable", environment)) + "\n" +
			"echo; cat ~/devkit-game/CarX_Street-argv.json; echo; cat ~/devkit-game/CarX_Street-env.json; echo\n" +
			$"cd /tmp && ~/devkit-game/CarX_Street/{DevkitGames.LaunchWrapper} -screen-fullscreen 1 'two words'";

		CommandResult result = Wsl.Run(script, Wsl.NewHome());
		Assert.True(result.Success, result.Combined);

		string[] lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal([$"{DevkitGames.LaunchWrapper} -screen-fullscreen 1 -profiler-enable"], JsonSerializer.Deserialize<string[]>(lines[0])!);
		Assert.Equal(new Dictionary<string, string> { ["SteamDeck"] = "1" }, JsonSerializer.Deserialize<Dictionary<string, string>>(lines[1])!);
		Assert.Equal(
		[
			"MANGOHUD=1",
			"CONFIG=" + MangoHudCapture.Hud + ",output_folder=/home/deck/m,autostart_log=1,log_duration=60",
			"ARG=-screen-fullscreen", "ARG=1", "ARG=two words"
		], lines[2..]);
	}

	// Без переменных на один запуск обёртки нет: Steam запускает саму игру
	[WslFact]
	public void LaunchWithoutExtrasRemovesWrapper()
	{
		string script =
			$"mkdir -p ~/devkit-game/CarX_Street && touch ~/devkit-game/CarX_Street/{DevkitGames.LaunchWrapper}\n" +
			DevkitGames.WriteLaunchFilesScript(Profile, new LaunchExtras("-x", null)) + "\n" +
			$"echo; cat ~/devkit-game/CarX_Street-argv.json; echo; if [ -e ~/devkit-game/CarX_Street/{DevkitGames.LaunchWrapper} ]; then echo LEFT; fi";

		CommandResult result = Wsl.Run(script, Wsl.NewHome());

		Assert.True(result.Success, result.Combined);
		Assert.Equal("[\"CarX_Street.x86_64 -screen-fullscreen 1 -x\"]", result.Output.Trim());
	}

	[WslFact]
	public void StopKillsOnlyGameProcesses()
	{
		string home = Wsl.NewHome();
		string script =
			"mkdir -p ~/devkit-game/CarX_Street/sub\n" +
			"cp /usr/bin/sleep ~/devkit-game/CarX_Street/CarX_Street.x86_64\n" +
			"~/devkit-game/CarX_Street/CarX_Street.x86_64 300 & game=$!\n" +
			"( cd ~/devkit-game/CarX_Street/sub && exec sleep 301 ) & helper=$!\n" +
			"sleep 302 & other=$!\n" +
			"sleep 0.3\n" +
			"n=$(" + DevkitGames.StopScript(Profile) + ")\n" +
			"echo \"stopped=$n\"\n" +
			"kill -0 $game 2>/dev/null && echo GAME_ALIVE\n" +
			"kill -0 $helper 2>/dev/null && echo HELPER_ALIVE\n" +
			"kill -0 $other 2>/dev/null && echo OTHER_ALIVE\n" +
			"kill $other";

		CommandResult result = Wsl.Run(script, home);

		Assert.Contains("stopped=2", result.Output);
		Assert.DoesNotContain("GAME_ALIVE", result.Output);
		Assert.DoesNotContain("HELPER_ALIVE", result.Output);
		Assert.Contains("OTHER_ALIVE", result.Output);
	}

	[WslFact]
	public void ScriptsAreValidShell()
	{
		foreach (string script in new[]
		         {
			         DevkitGames.DirectLaunchScript(Profile),
			         DevkitGames.DirectLaunchScript(Profile, new LaunchExtras(ProfilerCapture.Arguments("/home/deck/p/a.raw", 100), new Dictionary<string, string> { ["MANGOHUD"] = "1", ["MANGOHUD_CONFIG"] = MangoHudCapture.Hud })),
			         DevkitGames.StopScript(Profile),
			         DevkitGames.WriteLaunchFilesScript(Profile),
			         DevkitGames.SteamCommandScript("run-game/", "gameid=x", "/tmp/sdv-z"),
			         DeckStatus.Script
		         })
		{
			CommandResult check = Wsl.Run($"bash -n <<'SDV_EOF'\n{script}\nSDV_EOF\necho \"EXIT=$?\"");
			Assert.Contains("EXIT=0", check.Output);
		}
	}

	[WslFact]
	public void StatusScriptRuns()
	{
		CommandResult result = Wsl.Run(DeckStatus.Script, Wsl.NewHome());
		DeckStatus status = DeckStatus.Parse(result.Output);

		Assert.True(result.Success, result.Combined);
		Assert.NotEmpty(status["kernel"]);
		Assert.NotEmpty(status["uptime"]);
		Assert.Equal("0", status["steam"]);
	}
}

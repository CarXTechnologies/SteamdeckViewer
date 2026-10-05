using SteamdeckViewer.Core;

namespace SteamdeckViewer.Tests;

public sealed class MoonlightTests
{
	[Fact]
	public void StreamArgumentsForDeckScreen()
	{
		IReadOnlyList<string> args = Moonlight.StreamArguments("10.23.3.120", new StreamOptions(1280, 800, 60, 20000, Fullscreen: false, PerformanceOverlay: true));

		Assert.Equal(
			["stream", "10.23.3.120", "Desktop", "--display-mode", "windowed", "--resolution", "1280x800", "--fps", "60", "--bitrate", "20000",
				"--absolute-mouse", "--no-quit-after", "--performance-overlay"],
			args);
	}

	[Fact]
	public void PairArgumentsAndPin()
	{
		string pin = Moonlight.NewPin();
		Assert.Matches("^[0-9]{4}$", pin);
		Assert.Equal(["pair", "steamdeck", "--pin", pin], Moonlight.PairArguments("steamdeck", pin));
		Assert.Matches("^SteamdeckViewer-[A-Za-z0-9._-]+$", Moonlight.ClientName());
	}
}

public sealed class SunshineParsingTests
{
	[Theory]
	[InlineData("""{"status": true}""", true)]
	[InlineData("""{"status": "true"}""", true)]
	[InlineData("""{"status": false}""", false)]
	[InlineData("<html>Unauthorized</html>", false)]
	public void PinStatus(string json, bool expected)
	{
		Assert.Equal(expected, SunshineHost.IsStatusTrue(json));
	}

	[Fact]
	public void ClientNames()
	{
		Assert.Equal(["SteamdeckViewer-PC", "phone"],
			SunshineHost.ParseClientNames("""{"named_certs":[{"name":"SteamdeckViewer-PC","uuid":"1"},{"name":"phone","uuid":"2"}],"status":true}"""));
		Assert.Empty(SunshineHost.ParseClientNames("not json"));
	}

	[Theory]
	[InlineData("{\"status\":true}\n200", 200, "{\"status\":true}")]
	[InlineData("{\"error\":\"x\"}\r\n400\n", 400, "{\"error\":\"x\"}")]
	[InlineData("\n404", 404, "")]
	[InlineData("401", 401, "")]
	[InlineData("garbage", 0, "garbage")]
	public void CurlOutputCarriesHttpCode(string output, int code, string body)
	{
		Assert.Equal(new ApiResponse(code, body), SunshineHost.ParseCurlOutput(output));
	}

	// Ответ GET /api/pin Sunshine 2026.9
	[Fact]
	public void PendingPairings()
	{
		Assert.Equal(
			[new SunshinePairing("0123456789abcdef0123456789abcdef", "roth", "10.23.2.21"), new SunshinePairing("fedcba9876543210fedcba9876543210", "", "")],
			SunshineHost.ParsePendingPairings(
				"""{"pairings":[{"id":"0123456789abcdef0123456789abcdef","name":"roth","address":"10.23.2.21"},{"id":"fedcba9876543210fedcba9876543210"},{"name":"no id"}]}"""));
		Assert.Empty(SunshineHost.ParsePendingPairings("""{"pairings":[]}"""));
		Assert.Empty(SunshineHost.ParsePendingPairings("not json"));
	}

	[Fact]
	public void PickPairingPrefersNewRequestFromThisPc()
	{
		var old = new SunshinePairing("a", "roth", "10.23.2.21");
		var phone = new SunshinePairing("b", "roth", "10.23.3.50");
		var ours = new SunshinePairing("c", "roth", "10.23.2.21");
		var local = new HashSet<string> { "10.23.2.21", "10.23.3.84" };

		// Старый запрос с этого же ПК не берём, из новых — с адреса ПК
		Assert.Equal(ours, SunshineHost.PickPairing([old, phone, ours], new HashSet<string> { "a" }, local));

		// Адрес не совпал (NAT, VPN), но новый запрос один
		Assert.Equal(phone, SunshineHost.PickPairing([old, phone], new HashSet<string> { "a" }, new HashSet<string> { "192.168.1.5" }));

		// Несколько чужих новых запросов — не угадываем
		Assert.Null(SunshineHost.PickPairing([phone, ours], new HashSet<string>(), new HashSet<string> { "192.168.1.5" }));
		Assert.Null(SunshineHost.PickPairing([old], new HashSet<string> { "a" }, local));
	}

	[Fact]
	public void PinBodyForNewAndOldSunshine()
	{
		Assert.Equal("""{"pairing_id":"0123456789abcdef0123456789abcdef","pin":"1234","name":"PC"}""",
			System.Text.Json.JsonSerializer.Serialize(SunshineHost.PinBody("0123456789abcdef0123456789abcdef", "1234", "PC")));
		Assert.Equal("""{"pin":"1234","name":"PC"}""", System.Text.Json.JsonSerializer.Serialize(SunshineHost.PinBody(null, "1234", "PC")));
	}

	[Fact]
	public void GeneratedCredentialsAreShellAndCurlSafe()
	{
		SunshineCredentials credentials = SunshineCredentials.Generate();
		Assert.Matches("^[A-Za-z0-9]{20}$", credentials.Password);
		Assert.NotEqual(credentials.Password, SunshineCredentials.Generate().Password);
	}

	private static readonly DeckAccount Deck = new("deck", "1000", "1000", "/home/deck");

	[Fact]
	public void UnitsAndPolkitRuleContent()
	{
		string unit = SunshineHost.UnitFile(Deck);
		Assert.Contains("Environment=FLATPAK_BWRAP=/var/lib/steamdeckviewer/bwrap", unit);
		Assert.Contains("Environment=PULSE_SERVER=unix:/run/user/1000/pulse/native", unit);
		Assert.Contains("ExecStartPre=/usr/bin/install -o root -g root -m 4755 /usr/bin/bwrap /var/lib/steamdeckviewer/bwrap", unit);
		Assert.Contains("ExecStartPre=-/usr/bin/bash /var/lib/steamdeckviewer/sync-config.sh to-root\n", unit);
		Assert.Contains("ExecStart=/usr/bin/flatpak run --system --socket=wayland --env=QT_QPA_PLATFORM=offscreen dev.lizardbyte.app.Sunshine capture=kms\n", unit);
		Assert.Contains("ExecStopPost=-/usr/bin/bash /var/lib/steamdeckviewer/sync-config.sh to-user\n", unit);

		string sync = SunshineHost.SyncScript(Deck);
		Assert.Contains("root_cfg=/root/.var/app/dev.lizardbyte.app.Sunshine/config/sunshine\n", sync);
		Assert.Contains("user_cfg=/home/deck/.var/app/dev.lizardbyte.app.Sunshine/config/sunshine\n", sync);

		string desktop = SunshineHost.DesktopUnitFile();
		Assert.Contains("EnvironmentFile=-%h/.config/steamdeckviewer/sunshine-desktop.env\n", desktop);
		Assert.Contains("ExecStart=/usr/bin/flatpak run --system --env=CONFIGURATION_DIRECTORY=%h/.var/app/dev.lizardbyte.app.Sunshine/config " +
		                "dev.lizardbyte.app.Sunshine capture=${SDV_CAPTURE}\n", desktop);
		Assert.Contains("PartOf=graphical-session.target\n", desktop);

		string rule = SunshineHost.PolkitRule("deck");
		Assert.Contains("action.lookup(\"unit\") == \"sdv-sunshine.service\"", rule);
		Assert.Contains("subject.user == \"deck\"", rule);
		Assert.Equal("weird-user", SunshineHost.SafeName("weird\"user"));
	}

	// Исходники с CRLF (git с core.autocrlf): перевод строки Windows ломает bash на Deck, поэтому команды нормализуются перед отправкой
	[Fact]
	public void DeckScriptsUseUnixNewlines()
	{
		var credentials = new SunshineCredentials("sdv", "pass");
		var scripts = new Dictionary<string, string>
		{
			["status"] = SunshineHost.StatusScript,
			["start"] = SunshineHost.StartScript(forceComposite: true),
			["stop"] = SunshineHost.StopScript(),
			["install"] = SunshineHost.InstallScript(Deck, credentials, "deck"),
			["uninstall"] = SunshineHost.UninstallScript(Deck),
			["uninstall-user"] = SunshineHost.UninstallUserScript(),
			["deck-status"] = DeckStatus.Script
		};

		Assert.Empty(scripts.Where(s => Sh.UnixNewlines(s.Value).Contains('\r')).Select(s => s.Key));
		Assert.Equal("for x in 1; do\n  echo \"$x\"\ndone\n", Sh.UnixNewlines("for x in 1; do\r\n  echo \"$x\"\r\ndone\r\n"));
	}

	[Fact]
	public void StatusOfBothServices()
	{
		SunshineStatus game = SunshineHost.ParseStatus("app=1\nversion=2026.914.233613\nservice=1\nlayout=2\nrunning=1\ndesktop=0\nsession=game\nweb=1\n");
		Assert.True(game is { AppInstalled: true, Version: "2026.914.233613", Running: true, AnyRunning: true, Session: DeckScreenMode.GameMode });
		Assert.False(game.WrongInstance || game.NeedsUpgrade || game.ForeignInstance);

		// Deck переключили на рабочий стол, а работает служба Game Mode — картинка будет повёрнута
		Assert.True(SunshineHost.ParseStatus("service=1\nlayout=2\nrunning=1\ndesktop=0\nsession=desktop\nweb=1").WrongInstance);
		Assert.True(SunshineHost.ParseStatus("service=1\nlayout=2\nrunning=0\ndesktop=1\nsession=game\nweb=1").WrongInstance);
		Assert.False(SunshineHost.ParseStatus("service=1\nlayout=2\nrunning=0\ndesktop=1\nsession=desktop\nweb=1").WrongInstance);

		// Во время переключения режима не определён — ничего не трогаем
		Assert.False(SunshineHost.ParseStatus("service=1\nlayout=2\nrunning=1\nsession=none\nweb=1").WrongInstance);

		Assert.True(SunshineHost.ParseStatus("service=1\nlayout=1\nrunning=0\nsession=game").NeedsUpgrade);
		Assert.True(SunshineHost.ParseStatus("service=0\nrunning=0\ndesktop=0\nweb=1").ForeignInstance);
	}
}

public sealed class SunshineLinuxTests
{
	private const string AppId = SunshineHost.AppId;

	[WslFact]
	public void ScriptsAreValidShell()
	{
		var account = new DeckAccount("deck", "1000", "1000", "/home/deck");
		foreach (string script in new[]
		         {
			         SunshineHost.InstallScript(account, new SunshineCredentials("sdv", "p4ss'word"), "steamdeck"),
			         SunshineHost.UninstallScript(account),
			         SunshineHost.UninstallUserScript(),
			         SunshineHost.StartScript(forceComposite: true),
			         SunshineHost.StopScript(),
			         SunshineHost.StatusScript
		         })
		{
			CommandResult check = Wsl.Run($"bash -n <<'SDV_EOF'\n{script}\nSDV_EOF\necho \"EXIT=$?\"");
			Assert.True(check.Output.Contains("EXIT=0"), script + "\n" + check.Combined);
		}
	}

	[WslFact]
	public void StatusScriptOnHostWithoutSunshine()
	{
		CommandResult result = Wsl.Run(SunshineHost.StatusScript, Wsl.NewHome());
		SunshineStatus status = SunshineHost.ParseStatus(result.Output);

		Assert.False(status.AppInstalled, result.Combined);
		Assert.False(status.ServiceInstalled);
		Assert.False(status.AnyRunning);
		Assert.Equal(DeckScreenMode.Unknown, status.Session);
		Assert.Equal("1", DeckStatus.Parse(result.Output)["layout"]);
	}

	// Установочный скрипт с подменёнными flatpak, systemctl, chown и udevadm: службы, правила, настройки root и их копия
	// для рабочего стола (остатки прежней неудачной установки затираются), правило udev из пакета (или запасное).
	// Второй прогон — повторная установка после сопряжения на рабочем столе: оно не теряется
	[WslFact]
	public void InstallScriptWritesUnitsRulesAndBothConfigs()
	{
		string home = Wsl.NewHome();
		var account = new DeckAccount("deck", "1000", "1000", home);
		string script = SunshineHost.InstallScript(account, new SunshineCredentials("sdv", "Secr3tPass"), "my deck")
			.Replace(SunshineHost.UnitPath, "$HOME/root/unit")
			.Replace(SunshineHost.PolkitRulePath, "$HOME/root/polkit/rule")
			.Replace(Sh.ParentPath(SunshineHost.PolkitRulePath), "$HOME/root/polkit")
			.Replace(SunshineHost.UdevRulePath, "$HOME/root/udev/60-sunshine.rules")
			.Replace(Sh.ParentPath(SunshineHost.UdevRulePath), "$HOME/root/udev")
			.Replace(SunshineHost.ModulesLoadPath, "$HOME/root/modules/60-sunshine.conf")
			.Replace(Sh.ParentPath(SunshineHost.ModulesLoadPath), "$HOME/root/modules")
			.Replace(SunshineHost.StateDirectory, "$HOME/root/state")
			.Replace(SunshineHost.RootConfigDirectory, "$HOME/rootcfg");

		string cfg = SunshineHost.UserConfigDirectory(account);
		string fakes =
			$"mkdir -p ~/bin ~/rootcfg ~/app/files/share/sunshine/udev/rules.d {cfg}\n" +
			"printf 'capture = kms\\nmin_log_level = info\\n' > ~/rootcfg/sunshine.conf\n" +
			"echo '{\"paired\":\"moonlight\"}' > ~/rootcfg/sunshine_state.json\n" +
			$"echo '{{\"paired\":\"stale\"}}' > {cfg}/sunshine_state.json\ntouch -d @1000000000 {cfg}/sunshine_state.json\n" +
			"echo 'RULE FROM FLATPAK' > ~/app/files/share/sunshine/udev/rules.d/60-sunshine.rules\n" +
			"cat > ~/bin/flatpak <<'EOF'\n#!/bin/sh\necho \"flatpak $*\" >> \"$HOME/calls\"\n[ \"$1\" = info ] && echo \"$HOME/app\"\nexit 0\nEOF\n" +
			"for c in systemctl chown udevadm modprobe; do printf '#!/bin/sh\\necho \"%s $*\" >> \"$HOME/calls\"\\n' $c > ~/bin/$c; done\n" +
			"chmod +x ~/bin/*\n" +
			"export PATH=\"$HOME/bin:$PATH\"\n";

		string check = $"\necho \"EXIT=$?\"\necho ---; cat {cfg}/sunshine.conf; echo ---; cat {cfg}/sunshine_state.json ~/rootcfg/sunshine_state.json; " +
		               "echo ---; cat ~/calls; echo ---; cat ~/root/unit ~/root/polkit/rule; echo ---; cat ~/root/udev/60-sunshine.rules ~/root/modules/60-sunshine.conf\n";

		// Между прогонами Moonlight сопрягли на рабочем столе: состояние у deck новее, чем у root
		string pairedOnDesktop =
			"rm ~/calls ~/app/files/share/sunshine/udev/rules.d/60-sunshine.rules\n" +
			$"echo '{{\"paired\":\"desktop\"}}' > {cfg}/sunshine_state.json\ntouch -d @$(( $(date +%s) + 3600 )) {cfg}/sunshine_state.json\n" +
			"echo ===";

		CommandResult result = Wsl.Run(fakes + Wsl.Subshell(script) + check + pairedOnDesktop + Wsl.Subshell(script) + check, home);

		string[] runs = result.Output.Split("===\n");
		Assert.Equal(2, runs.Length);

		string[] first = runs[0].Split("---\n");
		Assert.True(first[0].Contains("EXIT=0"), result.Combined);

		// Настройки root (без capture — он задаётся в командной строке службы) скопированы deck, его старые остатки затёрты
		string conf = first[1];
		Assert.DoesNotContain("capture", conf);
		Assert.Contains("min_log_level = info\n", conf);
		Assert.Contains("encoder = vaapi\n", conf);
		Assert.Contains("adapter_name = /dev/dri/renderD128\n", conf);
		Assert.Contains("sunshine_name = my-deck\n", conf);
		Assert.Equal("{\"paired\":\"moonlight\"}\n{\"paired\":\"moonlight\"}\n", first[2]);

		string calls = first[3];
		Assert.Contains($"flatpak install --system --noninteractive --or-update flathub {AppId}", calls);
		Assert.Contains($"flatpak run --system --command=sunshine {AppId} --creds sdv Secr3tPass", calls);
		Assert.Contains($"chown -R 1000:1000 {cfg}", calls);
		Assert.Contains("udevadm trigger --property-match=DEVNAME=/dev/uinput", calls);
		Assert.Contains("modprobe uhid", calls);
		Assert.Contains("systemctl daemon-reload", calls);

		Assert.Contains("capture=kms", first[4]);
		Assert.Contains("sync-config.sh to-user", first[4]);
		Assert.Contains("subject.user == \"deck\"", first[4]);
		Assert.Equal("RULE FROM FLATPAK\nuhid\n", first[5]);

		// Повторная установка: сопряжение с рабочего стола у обеих служб, ключи не дублируются, без правила в пакете — запасное
		string[] second = runs[1].Split("---\n");
		Assert.True(second[0].Contains("EXIT=0"), result.Combined);
		Assert.Single(second[1].Split('\n'), l => l.StartsWith("encoder"));
		Assert.Equal("{\"paired\":\"desktop\"}\n{\"paired\":\"desktop\"}\n", second[2]);
		Assert.Contains("TAG+=\"uaccess\"", second[5]);
	}

	// Синхронизация каталогов двух служб: копируется только более свежая сторона, force — всегда, целиком (лишние файлы уходят)
	[WslFact]
	public void SyncScriptCopiesNewerSide()
	{
		string home = Wsl.NewHome();
		var account = new DeckAccount("deck", "1000", "1000", home);
		string cfg = SunshineHost.UserConfigDirectory(account);
		string sync = SunshineHost.SyncScript(account).Replace(SunshineHost.RootConfigDirectory, "$HOME/rootcfg");

		string script =
			"mkdir -p ~/bin ~/rootcfg " + cfg + "\n" +
			"printf '#!/bin/sh\\necho \"chown $*\" >> \"$HOME/calls\"\\n' > ~/bin/chown; chmod +x ~/bin/chown; export PATH=\"$HOME/bin:$PATH\"\n" +
			$"cat > ~/sync.sh <<'SDV_SYNC'\n{sync}SDV_SYNC\n" +
			"state() { echo \"$2\" > \"$1/sunshine_state.json\"; touch -d @$3 \"$1/sunshine_state.json\"; }\n" +
			"show() { echo \"$1 root=$(cat ~/rootcfg/sunshine_state.json) user=$(cat " + cfg + "/sunshine_state.json) extra=$(ls " + cfg + " | grep -c extra)\"; }\n" +
			"state ~/rootcfg root1 1000000000; state " + cfg + " user1 2000000000; touch " + cfg + "/extra\n" +
			"bash ~/sync.sh to-user; show A\n" +
			"bash ~/sync.sh to-root; show B\n" +
			"rm ~/rootcfg/extra; state ~/rootcfg root2 2100000000; bash ~/sync.sh to-user; show C\n" +
			"state ~/rootcfg root3 1500000000; bash ~/sync.sh to-user force; show D\n" +
			"bash ~/sync.sh bogus 2>/dev/null; echo \"BOGUS=$?\"";

		CommandResult result = Wsl.Run(script, home);

		Assert.Contains("A root=root1 user=user1 extra=1", result.Output);  // у root старее — deck не трогаем
		Assert.Contains("B root=user1 user=user1 extra=1", result.Output);  // у deck новее — к root
		Assert.Contains("C root=root2 user=root2 extra=0", result.Output);  // у root новее — к deck целиком
		Assert.Contains("D root=root3 user=root3 extra=0", result.Output);  // force — несмотря на возраст
		Assert.Contains("BOGUS=2", result.Output);
	}

	// Скрипт запуска выбирает службу по режиму Deck: подменённые pgrep, systemctl и flatpak, веб-интерфейс — заглушка на другом порту
	[WslFact]
	public void StartScriptPicksServiceByDeckMode()
	{
		const int port = 47993;
		string script = SunshineHost.StartScript(forceComposite: true).Replace($"/dev/tcp/127.0.0.1/{SunshineHost.WebPort}", $"/dev/tcp/127.0.0.1/{port}");

		string fakes =
			"mkdir -p ~/bin\n" +
			"cat > ~/bin/pgrep <<'EOF'\n#!/bin/sh\n" +
			"case \"$2\" in 'gamescope(-wl)?') [ \"$FAKE_SESSION\" = game ];; kwin_x11) [ \"$FAKE_SESSION\" = x11 ];; *) exit 1;; esac\nEOF\n" +
			"cat > ~/bin/systemctl <<'EOF'\n#!/bin/sh\necho \"systemctl $*\" >> \"$HOME/calls\"\n" +
			"[ -n \"$FAKE_FAIL\" ] && [ \"$1\" = start ] && { echo 'Failed to start sdv-sunshine.service: Access denied' >&2; exit 1; }\nexit 0\nEOF\n" +
			"for c in flatpak xprop; do printf '#!/bin/sh\\necho \"%s $*\" >> \"$HOME/calls\"\\n' $c > ~/bin/$c; done\n" +
			"chmod +x ~/bin/*\n" +
			"export PATH=\"$HOME/bin:$PATH\"\n" +
			$"python3 -m http.server {port} --bind 127.0.0.1 >/dev/null 2>&1 & srv=$!\n" +
			$"for i in $(seq 50); do (exec 3<>/dev/tcp/127.0.0.1/{port}) 2>/dev/null && break; sleep 0.1; done\n";

		string Run(string session, string extra = "")
		{
			return $"rm -f ~/calls\nexport FAKE_SESSION={session}{extra}" + Wsl.Subshell(script) +
			       $"\necho \"EXIT=$?\"\necho ---; cat ~/calls; echo ---; cat ~/{SunshineHost.DesktopEnvInHome} 2>/dev/null; rm -f ~/{SunshineHost.DesktopEnvInHome}; echo ===\n";
		}

		string home = Wsl.NewHome();
		CommandResult result = Wsl.Run(
			fakes + Run("game") + Run("wayland") + Run("x11") + Run("game", " FAKE_FAIL=1") +
			$"cat ~/.config/systemd/user/{SunshineHost.DesktopUnit}\nkill $srv", home);

		string[] runs = result.Output.Split("===\n");
		Assert.Equal(5, runs.Length);

		string[] game = runs[0].Split("---\n");
		Assert.True(game[0].Contains("mode=game") && game[0].Contains("EXIT=0"), result.Combined);
		Assert.Contains($"systemctl --user stop {SunshineHost.DesktopUnit}", game[1]);
		Assert.Contains($"systemctl start {SunshineHost.Unit}", game[1]);
		Assert.Contains("xprop -root -f GAMESCOPE_COMPOSITE_FORCE 32c -set GAMESCOPE_COMPOSITE_FORCE 1", game[1]);
		Assert.DoesNotContain("flatpak", game[1]);

		string[] wayland = runs[1].Split("---\n");
		Assert.True(wayland[0].Contains("mode=desktop") && wayland[0].Contains("EXIT=0"), result.Combined);
		Assert.Contains($"systemctl stop {SunshineHost.Unit}", wayland[1]);
		Assert.Contains($"flatpak permission-set kde-authorized remote-desktop {AppId} yes", wayland[1]);
		Assert.Contains("systemctl --user daemon-reload", wayland[1]);
		Assert.Contains($"systemctl --user start {SunshineHost.DesktopUnit}", wayland[1]);
		Assert.DoesNotContain("xprop", wayland[1]);
		Assert.Equal("SDV_CAPTURE=portal\n", wayland[2]);

		Assert.Equal("SDV_CAPTURE=x11\n", runs[2].Split("---\n")[2]);

		Assert.Contains("EXIT=3", runs[3]);

		Assert.Equal(SunshineHost.DesktopUnitFile(), runs[4]);
	}

	// Пароль доходит до sudo через stdin, скрипт выполняется и удаляется, неверный пароль распознаётся
	[WslFact]
	public void SudoWrapperPassesPasswordAndCleansUp()
	{
		string home = Wsl.NewHome();
		string fakeSudo =
			"mkdir -p ~/bin\n" +
			"cat > ~/bin/sudo <<'EOF'\n" +
			"#!/bin/bash\n" +
			"[ \"$1\" = -S ] || exit 9\n" +
			"shift 3\n" +
			"read -r pass\n" +
			"if [ \"$pass\" != 'right pass' ]; then echo 'Sorry, try again.' >&2; echo 'sudo: 1 incorrect password attempt' >&2; exit 1; fi\n" +
			"exec \"$@\"\n" +
			"EOF\n" +
			"chmod +x ~/bin/sudo\n" +
			"export PATH=\"$HOME/bin:$PATH\"\n";

		string path = "/tmp/sdv-root-test-" + Guid.NewGuid().ToString("N") + ".sh";
		string script =
			fakeSudo +
			$"printf 'echo ran-as-root; exit 5' | ( {DeckSudo.WriteCommand(path)} )\n" +
			$"stat -c 'mode=%a' {path}\n" +
			$"printf 'right pass\\n' | ( {DeckSudo.RunCommand(path)} ); echo \"EXIT=$?\"\n" +
			$"[ -e {path} ] && echo LEFT_BEHIND\n" +
			$"printf 'echo x' | ( {DeckSudo.WriteCommand(path)} )\n" +
			$"printf 'wrong\\n' | ( {DeckSudo.RunCommand(path)} ) 2> ~/err; echo \"EXIT2=$?\"\n" +
			"echo ---; cat ~/err";

		CommandResult result = Wsl.Run(script, home);

		Assert.Contains("mode=600", result.Output);
		Assert.Contains("ran-as-root", result.Output);
		Assert.Contains("EXIT=5", result.Output);
		Assert.DoesNotContain("LEFT_BEHIND", result.Output);
		Assert.Contains("EXIT2=1", result.Output);
		Assert.True(DeckSudo.IsWrongPassword(result.Output.Split("---\n")[1]));
	}

	// curl на Deck ходит в API Sunshine по конфигу из stdin. Поддельный HTTPS-сервер ведёт себя как Sunshine 2026.9:
	// проверяет логин, отдаёт ожидающие запросы и без pairing_id отвечает 400
	[WslFact]
	public void CurlConfigTalksToSunshineApi()
	{
		string home = Wsl.NewHome();
		var credentials = new SunshineCredentials("sdv", "Secr3t\"Pass\\x");
		const int port = 47991;
		const string id = "0123456789abcdef0123456789abcdef";

		const string server = """
			import base64, http.server, json, ssl, sys
			expected = "Basic " + base64.b64encode(sys.argv[1].encode()).decode()
			ID = "0123456789abcdef0123456789abcdef"
			class H(http.server.BaseHTTPRequestHandler):
			    def reply(self, code, body):
			        self.send_response(code); self.send_header("Content-Type", "application/json"); self.end_headers()
			        self.wfile.write(json.dumps(body).encode())
			    def denied(self):
			        if self.headers.get("Authorization") == expected: return False
			        self.reply(401, {"status": False}); return True
			    def body(self):
			        return json.loads(self.rfile.read(int(self.headers["Content-Length"])))
			    def do_GET(self):
			        if self.denied(): return
			        if self.path == "/api/pin": return self.reply(200, {"pairings": [{"id": ID, "name": "roth", "address": "10.23.2.21"}]})
			        self.reply(200, {"named_certs": [{"name": "SteamdeckViewer-PC", "uuid": "1"}], "status": True})
			    def do_POST(self):
			        if self.denied(): return
			        body = self.body()
			        if body.get("pairing_id") != ID:
			            return self.reply(400, {"error": "pairing_id must contain exactly 32 hexadecimal characters", "status": False, "status_code": 400})
			        self.reply(200, {"status": body == {"pairing_id": ID, "pin": "1234", "name": "SteamdeckViewer-PC"}})
			    def do_DELETE(self):
			        if self.denied(): return
			        self.reply(200, {"status": self.body() == {"pairing_id": ID}})
			    def log_message(self, *a): pass
			srv = http.server.HTTPServer(("127.0.0.1", int(sys.argv[2])), H)
			ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER); ctx.load_cert_chain("cert.pem", "key.pem")
			srv.socket = ctx.wrap_socket(srv.socket, server_side=True)
			srv.serve_forever()
			""";

		string baseUrl = $"https://127.0.0.1:{port}";
		string Json(string? pairingId) => System.Text.Json.JsonSerializer.Serialize(SunshineHost.PinBody(pairingId, "1234", "SteamdeckViewer-PC"));
		var requests = new Dictionary<string, string>
		{
			["pending"] = SunshineHost.CurlConfig(credentials, "GET", "/api/pin", null, baseUrl: baseUrl),
			["pin"] = SunshineHost.CurlConfig(credentials, "POST", "/api/pin", Json(id), baseUrl: baseUrl),
			["legacy"] = SunshineHost.CurlConfig(credentials, "POST", "/api/pin", Json(null), baseUrl: baseUrl),
			["cancel"] = SunshineHost.CurlConfig(credentials, "DELETE", "/api/pin", $$"""{"pairing_id":"{{id}}"}""", baseUrl: baseUrl),
			["list"] = SunshineHost.CurlConfig(credentials, "GET", "/api/clients/list", null, baseUrl: baseUrl),
			["bad"] = SunshineHost.CurlConfig(credentials with { Password = "wrong" }, "GET", "/api/clients/list", null, baseUrl: baseUrl)
		};

		string script =
			"openssl req -x509 -newkey rsa:2048 -nodes -keyout key.pem -out cert.pem -days 1 -subj /CN=localhost 2>/dev/null\n" +
			$"cat > server.py <<'SDV_PY'\n{server}\nSDV_PY\n" +
			$"python3 server.py {Sh.Quote(credentials.User + ":" + credentials.Password)} {port} & srv=$!\n" +
			"sleep 1\n" +
			string.Concat(requests.Select(r => $"printf '%s' {Sh.Quote(r.Value)} | curl -K -; echo \"@@{r.Key}=$?\"\n")) +
			"kill $srv";

		CommandResult result = Wsl.Run(script, home);

		// Ответ каждого запроса — текст до его метки @@имя=код_выхода_curl
		var responses = new Dictionary<string, ApiResponse>();
		int start = 0;
		foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(result.Output, @"@@(\w+)=(\d+)\n?"))
		{
			Assert.True(m.Groups[2].Value == "0", $"curl {m.Groups[1].Value}: {result.Combined}");
			responses[m.Groups[1].Value] = SunshineHost.ParseCurlOutput(result.Output[start..m.Index]);
			start = m.Index + m.Length;
		}

		Assert.Equal(requests.Keys.Order(), responses.Keys.Order());

		Assert.Equal(200, responses["pending"].Code);
		Assert.Equal([new SunshinePairing(id, "roth", "10.23.2.21")], SunshineHost.ParsePendingPairings(responses["pending"].Body));

		Assert.Equal(200, responses["pin"].Code);
		Assert.True(SunshineHost.IsStatusTrue(responses["pin"].Body), result.Combined);

		Assert.Equal(400, responses["legacy"].Code);
		Assert.Contains("pairing_id", responses["legacy"].Body);

		Assert.Equal(200, responses["cancel"].Code);
		Assert.True(SunshineHost.IsStatusTrue(responses["cancel"].Body), result.Combined);

		Assert.Equal(200, responses["list"].Code);
		Assert.Equal(["SteamdeckViewer-PC"], SunshineHost.ParseClientNames(responses["list"].Body));

		Assert.Equal(401, responses["bad"].Code);
	}
}

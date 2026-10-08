using SteamdeckViewer.Core;

namespace SteamdeckViewer.Tests;

public sealed class BugReportTests
{
	private const string StatusOutput = """
		host=steamdeckX
		os=SteamOS
		version=3.8.28
		build=20260915.1
		kernel=6.18.50-valve2-1-neptune
		battery=68
		battery_status=Discharging
		temp_k10temp=71500
		temp_amdgpu=75000
		mem=16000000 8000000
		session=gamescope
		steam=1
		cef=0
		""";

	[Fact]
	public void DisplaysDeckStatus()
	{
		DeckStatus status = DeckStatus.Parse(StatusOutput);

		Assert.Equal("SteamOS 3.8.28 (build 20260915.1)", status.Display("os", "10.23.2.25"));
		Assert.Equal("68 % (разряжается)", status.Display("battery", "10.23.2.25"));
		Assert.Equal("Game Mode (gamescope)", status.Display("session", "10.23.2.25"));
		Assert.Equal("10.23.2.25", status.Display("ip", "10.23.2.25"));
		Assert.Equal("—", status.Display("fan", "10.23.2.25"));
		Assert.All(DeckStatus.Rows, row => Assert.False(string.IsNullOrWhiteSpace(status.Display(row.Key, "h"))));
	}

	[Fact]
	public void DescribesReport()
	{
		const string log = """
			Initialize engine version: 6000.3.8f1 (1c7db571dde0)
			[Game] Landscape: DEV. Version: Version: 1.15.1, buildNumber: 794, full: 1.15.1 (794)
			Something broke
			UnityEngine.Debug:LogError(Object)

			Careful
			UnityEngine.Debug:LogWarning(Object)

			NullReferenceException: Object reference not set to an instance of an object
			  at CarX.Street.Foo.Bar () [0x00000] in <00000000000000000000000000000000>:0

			""";

		var profile = new BuildProfile { LocalFolder = @"D:\Git\street_reserve\Build", Arguments = "-screen-fullscreen 1" };
		var data = new BugReportData(new DateTime(2026, 10, 8, 12, 30, 0), "1.0.1", "ROTH", profile, new DateTime(2026, 10, 7, 15, 40, 0),
			DeckStatus.Parse(StatusOutput), "10.23.2.25",
			new Dictionary<string, string> { ["steam_client"] = "1759461205", ["build_time"] = "1791380700", ["build_size"] = "24696061952", ["game_processes"] = "1" },
			log, null, null, ["Player.log", "screenshot.png"], ["Player-prev.log: нет на Deck (/home/deck/x)"]);

		string report = BugReport.Describe(data);

		Assert.Contains("Отчёт CarX Deck Tools 1.0.1, 2026-10-08 12:30:00, ПК ROTH", report);
		Assert.Contains(@"На ПК: D:\Git\street_reserve\Build, CarX_Street.x86_64 от 2026-10-07 15:40", report);
		Assert.Contains("На Deck: ~/devkit-game/CarX_Street, CarX_Street.x86_64 от ", report);
		Assert.Contains("Игра сейчас: запущена", report);
		Assert.Contains("Player.log: Initialize engine version: 6000.3.8f1 (1c7db571dde0)", report);
		Assert.Contains("Player.log: [Game] Landscape: DEV. Version: Version: 1.15.1, buildNumber: 794, full: 1.15.1 (794)", report);
		Assert.Contains("== Player.log: ошибок 2, предупреждений 1", report);
		Assert.Contains("  Something broke", report);
		Assert.Contains("  NullReferenceException: Object reference not set to an instance of an object", report);
		Assert.Contains("Steam: запущен, клиент 1759461205", report);
		Assert.Contains("Батарея: 68 % (разряжается)", report);
		Assert.Contains("замеров нет", report);
		Assert.Contains("Player.log, screenshot.png", report);
		Assert.Contains("не собрано — Player-prev.log: нет на Deck", report);
	}

	[Fact]
	public void DescribesReportWithoutLogAndStatus()
	{
		var data = new BugReportData(DateTime.Now, "1.0.1", "PC", new BuildProfile(), null, null, "h",
			new Dictionary<string, string>(), null, null, null, [], []);

		string report = BugReport.Describe(data);

		Assert.Contains("== Player.log не скачан", report);
		Assert.Contains("сборки на Deck нет", report);
		Assert.Contains("состояние не получено", report);
		Assert.Contains("Игра сейчас: не запущена", report);
	}

	[WslFact]
	public void InfoScriptReadsDeck()
	{
		var profile = new BuildProfile();
		string home = Wsl.NewHome();
		string script = $"""
			mkdir -p ~/devkit-game/CarX_Street ~/.steam/steam/package
			printf '"version"\t\t"1759461205"\n' > ~/.steam/steam/package/steam_client_steamdeck_stable_ubuntu12.manifest
			cp /bin/sleep ~/devkit-game/CarX_Street/CarX_Street.x86_64
			touch -d @1791380700 ~/devkit-game/CarX_Street/CarX_Street.x86_64
			~/devkit-game/CarX_Street/CarX_Street.x86_64 30 &
			sleep 0.3
			{Wsl.Subshell(BugReport.InfoScript(profile))}
			kill %1
			""";

		DeckStatus info = DeckStatus.Parse(Wsl.Run(script, home).Output);

		Assert.Equal("1759461205", info["steam_client"]);
		Assert.Equal("1791380700", info["build_time"]);
		Assert.True(long.Parse(info["build_size"]) > 0);
		Assert.Equal("1", info["game_processes"]);
	}
}

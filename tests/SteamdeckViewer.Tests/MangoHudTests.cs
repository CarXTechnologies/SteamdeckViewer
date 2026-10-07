using System.Globalization;
using SteamdeckViewer.Core;

namespace SteamdeckViewer.Tests;

public sealed class MangoHudTests
{
	[Fact]
	public void EnvironmentFollowsProfile()
	{
		Assert.Null(MangoHudCapture.Environment(new BuildProfile(), "/home/deck/m"));

		var overlay = MangoHudCapture.Environment(new BuildProfile { MangoHudOverlay = true }, "/home/deck/m")!;
		Assert.Equal("1", overlay["MANGOHUD"]);
		Assert.Equal(MangoHudCapture.Hud, overlay["MANGOHUD_CONFIG"]);

		// Только запись: оверлей скрыт, но MangoHud пишет CSV с первой секунды
		var log = MangoHudCapture.Environment(new BuildProfile { MangoHudLog = true }, "/home/deck/m")!;
		Assert.Equal("no_display,output_folder=/home/deck/m,autostart_log=1", log["MANGOHUD_CONFIG"]);
	}

	[Fact]
	public void LaunchEnvironmentOverridesProfile()
	{
		var profile = new BuildProfile { EnvironmentVariables = "SteamDeck=1 MANGOHUD=0" };
		IReadOnlyDictionary<string, string> env = profile.EnvironmentWith(new Dictionary<string, string> { ["MANGOHUD"] = "1" });
		Assert.Equal("1", env["SteamDeck"]);
		Assert.Equal("1", env["MANGOHUD"]);
		Assert.Equal("0", profile.EnvironmentWith(null)["MANGOHUD"]);
	}

	[Fact]
	public void PicksLatestLogButNotSummary()
	{
		var files = new Dictionary<string, (long Size, long MTime)>
		{
			["CarX_Street.x86_64_2026-10-07_12-00-00.csv"] = (10, 1000),
			["CarX_Street.x86_64_2026-10-07_13-00-00.csv"] = (20, 3000),
			["CarX_Street.x86_64_2026-10-07_13-00-00_summary.csv"] = (1, 3001)
		};

		Assert.Equal(("CarX_Street.x86_64_2026-10-07_13-00-00.csv", 20L), MangoHudCapture.Latest(files));
	}

	// Формат MangoHud 0.8: сведения о системе, строка столбцов и по строке на кадр; elapsed — наносекунды с начала записи
	[Fact]
	public void SummarizesLog()
	{
		var csv = new List<string>
		{
			"os,cpu,gpu,ram,kernel,driver,cpuscheduler",
			"SteamOS,AMD Custom APU 0405,AMD Custom GPU 0405,14.5 GB,6.18,Mesa 26.2,",
			"fps,frametime,cpu_load,cpu_power,gpu_load,cpu_temp,gpu_temp,gpu_core_clock,gpu_mem_clock,gpu_vram_used,gpu_power,ram_used,swap_used,process_rss,cpu_mhz,elapsed"
		};

		// 98 кадров по 16 мс, один в 40 мс и один в 100 мс: 1% low — второй по тяжести кадр, 0,1% low — самый тяжёлый
		double[] frametimes = [.. Enumerable.Repeat(16.0, 98), 40.0, 100.0];
		long elapsed = 0;
		for (int i = 0; i < frametimes.Length; i++)
		{
			elapsed += (long)(frametimes[i] * 1_000_000);
			csv.Add(string.Create(CultureInfo.InvariantCulture, $"{1000 / frametimes[i]:0.0},{frametimes[i]},{40 + i % 2 * 10},8.5,90,{70 + i % 5},{60 + i % 3},1600,800,2.5,7.5,6.{i % 10},0,1.2,2400,{elapsed}"));
		}

		MangoHudSummary summary = MangoHudCapture.Summarize(string.Join("\r\n", csv) + "\r\n")!;

		Assert.Equal(100, summary.Frames);
		Assert.Equal((elapsed - 16_000_000) / 1e9, summary.Seconds, 6);
		Assert.Equal(1000 / ((98 * 16.0 + 40 + 100) / 100), summary.AverageFps, 6);
		Assert.Equal(1000 / 40.0, summary.Low1Fps, 6);
		Assert.Equal(1000 / 100.0, summary.Low01Fps, 6);
		Assert.Equal(100, summary.MaxFrameTime);
		Assert.Equal(45, summary.CpuLoad!.Value, 6);
		Assert.Equal(90, summary.GpuLoad!.Value, 6);
		Assert.Equal(74, summary.CpuTempMax);
		Assert.Equal(62, summary.GpuTempMax);
		Assert.Equal(7.5, summary.GpuPower!.Value, 6);
		Assert.Equal(6.9, summary.RamMax!.Value, 6);
		Assert.Equal(2.5, summary.VramMax);

		string text = summary.Describe();
		Assert.Contains("1% low 25,0", text);
		Assert.Contains("GPU: загрузка 90 %, до 62 °C, 7,5 Вт в среднем", text);
	}

	[Fact]
	public void NoFramesNoSummary()
	{
		Assert.Null(MangoHudCapture.Summarize("os,cpu\nSteamOS,AMD\n"));
		Assert.Null(MangoHudCapture.Summarize("fps,frametime,elapsed\n"));
	}
}

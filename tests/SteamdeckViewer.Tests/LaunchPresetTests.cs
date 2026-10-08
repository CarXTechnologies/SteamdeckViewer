using System.Text.Json;
using SteamdeckViewer.Core;

namespace SteamdeckViewer.Tests;

public sealed class LaunchPresetTests
{
	private static readonly LaunchPreset Vulkan = new() { Name = "Vulkan", Arguments = " -force-vulkan ", EnvironmentVariables = "DXVK_HUD=fps MANGOHUD=0" };

	[Fact]
	public void PresetGoesBeforeProgramExtras()
	{
		LaunchExtras extras = LaunchExtras.Combine(Vulkan, "-profiler-enable", new Dictionary<string, string> { ["MANGOHUD"] = "1" });

		Assert.Equal("-force-vulkan -profiler-enable", extras.Arguments);
		Assert.Equal("fps", extras.Environment!["DXVK_HUD"]);
		// MangoHud включает программа по галкам профиля — это важнее набора
		Assert.Equal("1", extras.Environment["MANGOHUD"]);
	}

	[Fact]
	public void EmptyExtrasStayEmpty()
	{
		LaunchExtras none = LaunchExtras.Combine(null, null, null);
		Assert.Null(none.Arguments);
		Assert.Null(none.Environment);

		LaunchExtras blank = LaunchExtras.Combine(new LaunchPreset { Name = "пустой" }, " ", new Dictionary<string, string>());
		Assert.Null(blank.Arguments);
		Assert.Null(blank.Environment);
	}

	[Fact]
	public void PresetsSurviveSettings()
	{
		var profile = new BuildProfile { Presets = [Vulkan, new LaunchPreset { Name = "Без звука", Arguments = "-nosound" }], SelectedPreset = "Без звука" };

		BuildProfile restored = JsonSerializer.Deserialize<BuildProfile>(JsonSerializer.Serialize(profile))!;

		Assert.Equal(["Vulkan", "Без звука"], restored.Presets.Select(p => p.Name));
		Assert.Equal("-nosound", restored.ActivePreset!.Arguments);

		restored.SelectedPreset = "удалённый";
		Assert.Null(restored.ActivePreset);
		Assert.Null(new BuildProfile().ActivePreset);
	}
}

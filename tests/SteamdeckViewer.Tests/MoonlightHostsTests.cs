using Microsoft.Win32;
using SteamdeckViewer.Core;

namespace SteamdeckViewer.Tests;

public sealed class MoonlightHostsTests
{
	private const string Deck = "10.23.2.25";
	private const string Name = "steamdeckX";
	private const string Uuid = "ED180AFE-8DB6-F326-C513-3D127F3C8355";

	[Fact]
	public void ReadsSunshineIdentity()
	{
		const string serverInfo = """
			<?xml version="1.0" encoding="utf-8"?>
			<root status_code="200"><hostname>steamdeckX</hostname><appversion>7.1.431.-1</appversion>
			<uniqueid>ED180AFE-8DB6-F326-C513-3D127F3C8355</uniqueid><PairStatus>0</PairStatus></root>
			""";

		Assert.Equal((Name, Uuid), SunshineHost.ParseServerIdentity(serverInfo));
		Assert.Null(SunshineHost.ParseServerIdentity(""));
	}

	[Fact]
	public void DecidesPerHost()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		// Прежний Sunshine этого Deck: тот же адрес и имя, другой uuid
		Assert.Equal(MoonlightHostFix.Remove, MoonlightHosts.Decide(new MoonlightHost(1, Name, "4EFC9474", [Deck, "83.239.38.74"]), Deck, Name, Uuid));
		// Другой ПК, у которого раньше был этот адрес
		Assert.Equal(MoonlightHostFix.ForgetAddress, MoonlightHosts.Decide(new MoonlightHost(2, "steamdeck", "AAAA", [Deck]), Deck, Name, Uuid));
		// Тот самый Sunshine, регистр uuid не важен
		Assert.Equal(MoonlightHostFix.Keep, MoonlightHosts.Decide(new MoonlightHost(3, Name, Uuid.ToLowerInvariant(), [Deck]), Deck, Name, Uuid));
		// Тот же Deck, но по другому адресу (Wi-Fi) — сейчас не мешает
		Assert.Equal(MoonlightHostFix.Keep, MoonlightHosts.Decide(new MoonlightHost(4, Name, "5897B4D9", ["10.23.3.120"]), Deck, Name, Uuid));
	}

	// Раскладка как у Moonlight-qt: hosts\size (QWORD) и hosts\1…N с вложенным массивом apps
	[Fact]
	public void RemovesStaleHostsAndRenumbers()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		string path = @"Software\SteamdeckViewerTests\" + Guid.NewGuid().ToString("N");
		try
		{
			using RegistryKey moonlight = Registry.CurrentUser.CreateSubKey(path);
			using (RegistryKey hosts = moonlight.CreateSubKey("hosts"))
			{
				hosts.SetValue("size", 3L, RegistryValueKind.QWord);
				AddHost(hosts, 1, Name, "4EFC9474", Deck, withApps: true);
				AddHost(hosts, 2, "steamdeck", "AAAA", Deck, withApps: false);
				AddHost(hosts, 3, "office-pc", "BBBB", "10.23.2.40", withApps: true);
			}

			Assert.Equal(2, MoonlightHosts.FindStale(moonlight, Deck, Name, Uuid).Count);
			var fixes = MoonlightHosts.Fix(moonlight, Deck, Name, Uuid);

			Assert.Equal([(1, MoonlightHostFix.Remove), (2, MoonlightHostFix.ForgetAddress)], fixes.Select(f => (f.Host.Index, f.Fix)));
			using RegistryKey after = moonlight.OpenSubKey("hosts")!;
			Assert.Equal(RegistryValueKind.QWord, after.GetValueKind("size"));
			Assert.Equal(2L, after.GetValue("size"));
			Assert.Null(after.OpenSubKey("3"));

			using (RegistryKey first = after.OpenSubKey("1")!)
			{
				Assert.Equal("AAAA", first.GetValue("uuid"));
				Assert.Equal("", first.GetValue("localaddress"));
				Assert.Equal("", first.GetValue("manualaddress"));
				Assert.Equal(47989, first.GetValue("localport"));
				Assert.Equal(RegistryValueKind.DWord, first.GetValueKind("localport"));
			}

			using (RegistryKey second = after.OpenSubKey("2")!)
			{
				Assert.Equal("BBBB", second.GetValue("uuid"));
				Assert.Equal("10.23.2.40", second.GetValue("localaddress"));
				using RegistryKey app = second.OpenSubKey(@"apps\1")!;
				Assert.Equal("Desktop", app.GetValue("name"));
			}

			Assert.Empty(MoonlightHosts.Fix(moonlight, Deck, Name, Uuid));
		}
		finally
		{
			Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
		}
	}

	private static void AddHost(RegistryKey hosts, int index, string name, string uuid, string address, bool withApps)
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using RegistryKey host = hosts.CreateSubKey(index.ToString());
		host.SetValue("hostname", name);
		host.SetValue("uuid", uuid);
		host.SetValue("localaddress", address);
		host.SetValue("localport", 47989, RegistryValueKind.DWord);
		host.SetValue("manualaddress", address);
		host.SetValue("remoteaddress", "83.239.38.74");
		host.SetValue("srvcert", "-----BEGIN CERTIFICATE-----");
		if (withApps)
		{
			using RegistryKey apps = host.CreateSubKey("apps");
			apps.SetValue("size", 1L, RegistryValueKind.QWord);
			using RegistryKey app = apps.CreateSubKey("1");
			app.SetValue("name", "Desktop");
			app.SetValue("id", 881448767, RegistryValueKind.DWord);
		}
	}
}

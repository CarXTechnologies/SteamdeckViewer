using SteamdeckViewer.Core;

namespace SteamdeckViewer.Tests;

public sealed class FileFilterTests
{
	private readonly FileFilter m_filter = new(new BuildProfile().Excludes + " *.pdb Logs/ CarX_Street_Data/Raw/*");

	[Theory]
	[InlineData("CarX_Street_BackUpThisFolder_ButDontShipItWithYourGame/il2cpp.cpp")]
	[InlineData("CarX_Street_BurstDebugInformation_DoNotShip/Data/lib_burst_generated.txt")]
	[InlineData("CarX_Street_Data/Managed/Assembly.pdb")]
	[InlineData("Logs/today.log")]
	[InlineData("sub/Logs/x.txt")]
	[InlineData("CarX_Street_Data/Raw/file.bin")]
	public void Excludes(string path)
	{
		Assert.True(m_filter.IsExcluded(path));
	}

	[Theory]
	[InlineData("CarX_Street.x86_64")]
	[InlineData("GameAssembly.so")]
	[InlineData("CarX_Street_Data/Managed/Assembly.dll")]
	[InlineData("Logs")]
	[InlineData("CarX_Street_Data/RawOther/file.bin")]
	[InlineData("Other/CarX_Street_Data/Raw/file.bin")]
	public void Keeps(string path)
	{
		Assert.False(m_filter.IsExcluded(path));
	}
}

public sealed class BuildProfileTests
{
	[Fact]
	public void GameIdIsSafeForSteamUrl()
	{
		Assert.Equal("CarX_Street", new BuildProfile { Name = "CarX Street" }.GameId);
		Assert.Equal("a_b_c_1.2", new BuildProfile { Name = "a/b&c_1.2" }.GameId);
		Assert.Equal("game", new BuildProfile { Name = "  " }.GameId);
		Assert.Equal("_2fast", new BuildProfile { Name = "2fast" }.GameId);
		Assert.Equal("x_", new BuildProfile { Name = "x" }.GameId);

		// Шаблон id из devkit-клиента Valve: с дефисом Steam отвечает на create-shortcut «missing/invalid arguments»
		foreach (string name in new[] { "CarX Street", "CarX-Street", "2fast", "x", "Игра", ".hidden", "a b-c.d" })
		{
			Assert.Matches("^[A-Za-z_][A-Za-z0-9_.]+$", new BuildProfile { Name = name }.GameId);
		}

		Assert.Equal("CarX-Street", new BuildProfile { Name = "CarX Street" }.LegacyGameId);
	}

	[Fact]
	public void StartCommandQuotesExecutableWithSpaces()
	{
		Assert.Equal("CarX_Street.x86_64 -logFile x", new BuildProfile { Executable = "CarX_Street.x86_64", Arguments = " -logFile x " }.StartCommand);
		Assert.Equal("\"bin/My Game.x86_64\"", new BuildProfile { Executable = "bin\\My Game.x86_64", Arguments = "" }.StartCommand);
	}

	[Fact]
	public void ParsesEnvironment()
	{
		IReadOnlyDictionary<string, string> env = new BuildProfile { EnvironmentVariables = "SteamDeck=1 MANGOHUD=1;\nA=b=c junk" }.ParseEnvironment();
		Assert.Equal(3, env.Count);
		Assert.Equal("1", env["SteamDeck"]);
		Assert.Equal("1", env["MANGOHUD"]);
		Assert.Equal("b=c", env["A"]);
	}
}

public sealed class UnityPlayerLogTests
{
	private const string Multicast =
		"Multi-casting \"[IP] 192.168.1.42 [Port] 55000 [Flags] 3 [Guid] 2747325034 [EditorId] 1234 [Version] 1048832 " +
		"[Id] LinuxPlayer(13,192.168.1.42) [Debug] 1 [PackageName] LinuxPlayer [ProjectName] CarX Street\" to [225.0.0.222:54997]...";

	[Fact]
	public void ParsesMulticastAnnounce()
	{
		UnityPlayerInfo? info = UnityPlayerLog.Parse(Multicast, null);

		Assert.NotNull(info);
		Assert.Equal("192.168.1.42", info.Ip);
		Assert.Equal("CarX Street", info.ProjectName);
		Assert.Equal(56000 + (int)(2747325034L % 1000), info.DebuggerPort);
		Assert.StartsWith("[IP] 192.168.1.42", info.Announce);
	}

	[Fact]
	public void ExplicitDebuggerPortWins()
	{
		UnityPlayerInfo? info = UnityPlayerLog.Parse("Starting managed debugger on port 56123", null);
		info = UnityPlayerLog.Parse(Multicast, info);

		Assert.Equal(56123, info!.DebuggerPort);
	}

	[Fact]
	public void NoDebuggerPortWithoutScriptDebugging()
	{
		UnityPlayerInfo? info = UnityPlayerLog.Parse(Multicast.Replace("[Debug] 1", "[Debug] 0"), null);
		Assert.Null(info!.DebuggerPort);
	}

	[Fact]
	public void IgnoresOtherLines()
	{
		Assert.Null(UnityPlayerLog.Parse("Initialize engine version: 6000.3.8f1", null));
	}
}

public sealed class StatusParseTests
{
	[Fact]
	public void ParsesKeyValues()
	{
		DeckStatus status = DeckStatus.Parse("session=gamescope\nsteam=1\ntemp_k10temp=51200\nos=SteamOS\n\ngarbage");
		Assert.True(status.IsGameMode);
		Assert.True(status.SteamRunning);
		Assert.Equal("51.2 °C", status.Temperature("k10temp").Replace(',', '.'));
		Assert.Equal("—", status.Temperature("amdgpu"));
	}

	[Fact]
	public void ParsesRemoteListing()
	{
		var listing = FolderSync.ParseListing("a.txt\t12\t1700000000.9876543210\nsub dir/b\t0\t1700000001.0000000000\nbroken line\n");
		Assert.Equal((12L, 1700000000L), listing["a.txt"]);
		Assert.Equal((0L, 1700000001L), listing["sub dir/b"]);
		Assert.Equal(2, listing.Count);
	}

	[Fact]
	public void ParsesHashLines()
	{
		string hash = new string('a', 63) + "f";

		Assert.True(FolderSync.TryParseHashLine(hash + "  sub dir/file.so", out string path, out string parsed));
		Assert.Equal(("sub dir/file.so", hash), (path, parsed));
		Assert.True(FolderSync.TryParseHashLine(hash + " *bin", out path, out _));
		Assert.Equal("bin", path);

		Assert.False(FolderSync.TryParseHashLine("\\" + hash + "  back\\\\slash", out _, out _));
		Assert.False(FolderSync.TryParseHashLine(hash.ToUpperInvariant() + "  x", out _, out _));
		Assert.False(FolderSync.TryParseHashLine(hash + "  ", out _, out _));
		Assert.False(FolderSync.TryParseHashLine("sha256sum: missing: No such file or directory", out _, out _));
	}
}

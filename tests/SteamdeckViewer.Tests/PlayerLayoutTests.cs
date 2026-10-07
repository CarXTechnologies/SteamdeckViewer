using System.Security.Cryptography;
using System.Text;
using SteamdeckViewer.Core;

namespace SteamdeckViewer.Tests;

public sealed class PlayerLayoutTests
{
	private const string IndexPath = "CarX_Street_Data/StreamingAssets/aa/StandaloneLinux64/player_layout.bundle";
	private const string Bundle = "CarX_Street_Data/StreamingAssets/aa/StandaloneLinux64/47c82a73eb49252b68f5de3d216a326c.bundle";

	private static readonly Dictionary<string, byte[]> Build = new()
	{
		["GameAssembly.so"] = Encoding.UTF8.GetBytes("game assembly"),
		["CarX_Street.x86_64"] = Encoding.UTF8.GetBytes("#!launcher"),
		["libdecor-0.so.0"] = [1, 2, 3],
		[Bundle] = [4, 5]
	};

	// Индекс в формате BuildProcessSaveLayout из проекта игры
	internal static byte[] WriteIndex(IReadOnlyDictionary<string, byte[]> files)
	{
		using var payload = new MemoryStream();
		using (var writer = new BinaryWriter(payload, Encoding.UTF8, true))
		{
			writer.Write(0x31594C50u);
			writer.Write(2);
			writer.Write(0xCCCDF402u);
			writer.Write(files.Count);
			foreach ((string path, byte[] content) in files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
			{
				writer.Write(path);
				writer.Write((long)content.Length);
				writer.Write(SHA256.HashData(content));
			}
		}

		using var index = new MemoryStream();
		using (var writer = new BinaryWriter(index, Encoding.UTF8, true))
		{
			writer.Write((int)payload.Length);
			writer.Write(payload.ToArray());
			writer.Write(256);
			writer.Write(new byte[256]);
		}

		return index.ToArray();
	}

	private static PlayerLayout Layout(out byte[] index)
	{
		index = WriteIndex(Build);
		return PlayerLayout.Parse(index, IndexPath);
	}

	// Состояние «Deck» из набора файлов: листинг с размерами и хеши всего, что проверяет игра, плюс самого индекса
	private static List<string> Check(PlayerLayout layout, byte[] index, IDictionary<string, byte[]> deck)
	{
		var files = deck.ToDictionary(f => f.Key, f => ((long)f.Value.Length, 0L));
		files[IndexPath] = (index.Length, 0L);
		var hashes = deck.ToDictionary(f => f.Key, f => Convert.ToHexStringLower(SHA256.HashData(f.Value)));
		hashes[IndexPath] = Convert.ToHexStringLower(SHA256.HashData(index));
		List<string> mapped = deck.Keys.Where(p => layout.IsMappedByName(p) || PlayerLayout.NeedsHeader(p) && deck[p].AsSpan().StartsWith("#!"u8)).ToList();
		return layout.Compare(files, mapped, hashes);
	}

	[Fact]
	public void ParsesIndexWrittenByGameBuild()
	{
		PlayerLayout layout = Layout(out byte[] index);

		Assert.Equal(4, layout.Count);
		Assert.Equal(IndexPath, layout.IndexPath);
		Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(index)), layout.IndexDigest);

		Assert.Throws<InvalidDataException>(() => PlayerLayout.Parse([.. index, 0], IndexPath));
		Assert.Throws<InvalidDataException>(() => PlayerLayout.Parse(index[..^10], IndexPath));
		Assert.Throws<InvalidDataException>(() => PlayerLayout.Parse([], IndexPath));
	}

	// Те же правила, что PlayerLayoutFilter.IsMappedFile в игре
	[Theory]
	[InlineData("GameAssembly.so", true)]
	[InlineData("CarX_Street_Data/Plugins/libEOSSDK-Linux-Shipping.SO", true)]
	[InlineData("libdecor-0.so.0", true)]
	[InlineData("lib/libfoo.so.1.2.3", true)]
	[InlineData("CarX_Street.x86_64", true)]
	[InlineData("tools/a.dll", true)]
	[InlineData("tools/a.exe", true)]
	[InlineData("Other/47C82A73EB49252B68F5DE3D216A326C.bundle", true)]
	[InlineData("CarX_Street_Data/StreamingAssets/aa/StandaloneLinux64/other.bundle", false)]
	[InlineData("config.so.bak", false)]
	[InlineData("data.so.txt", false)]
	[InlineData("steam_appid.txt", false)]
	[InlineData("carx-deck-tools-launch.sh", false)]
	[InlineData("CarX_Street_Data/level0", false)]
	public void PicksFilesLikeTheGame(string path, bool mapped)
	{
		Assert.Equal(mapped, Layout(out _).IsMappedByName(path));
	}

	[Theory]
	[InlineData("CarX_Street_Data/level0", true)]
	[InlineData("some.dir/run", true)]
	[InlineData("trailing.", true)]
	[InlineData(".hidden", false)]
	[InlineData("a.txt", false)]
	public void HeaderDecidesForFilesWithoutExtension(string path, bool needsHeader)
	{
		Assert.Equal(needsHeader, PlayerLayout.NeedsHeader(path));
	}

	[Theory]
	[InlineData("7f454c46", true)]
	[InlineData("2321", false)]
	[InlineData("23212f62", true)]
	[InlineData("cffaedfe", true)]
	[InlineData("cafebabe", true)]
	[InlineData("554e4954", false)]
	[InlineData("7f45", false)]
	[InlineData("zz454c46", false)]
	public void RecognizesExecutableHeaders(string hex, bool executable)
	{
		Assert.Equal(executable, PlayerLayout.IsExecutableHeader(hex));
	}

	[Fact]
	public void PassesWhenDeckMatchesBuild()
	{
		PlayerLayout layout = Layout(out byte[] index);
		var deck = new Dictionary<string, byte[]>(Build) { ["steam_appid.txt"] = [7], ["CarX_Street_Data/level0"] = [0, 0, 0, 0] };

		Assert.Empty(Check(layout, index, deck));
	}

	[Fact]
	public void NamesEveryKindOfMismatch()
	{
		PlayerLayout layout = Layout(out byte[] index);
		var deck = new Dictionary<string, byte[]>(Build)
		{
			["GameAssembly.so"] = Encoding.UTF8.GetBytes("game assemblX"),
			["libdecor-0.so.0"] = [1, 2],
			["CarX_Street_Data/Plugins/old.so"] = [9],
			["tools/run"] = Encoding.UTF8.GetBytes("#!/bin/sh")
		};
		deck.Remove(Bundle);

		Assert.Equal(
		[
			"лишний файл: CarX_Street_Data/Plugins/old.so",
			"другое содержимое: GameAssembly.so",
			"другой размер: libdecor-0.so.0",
			"лишний файл: tools/run",
			"нет на Deck: " + Bundle
		], Check(layout, index, deck));
	}

	[Fact]
	public void IndexOnDeckMustBeTheOnlyOneAndTheSame()
	{
		PlayerLayout layout = Layout(out byte[] index);
		var files = Build.ToDictionary(f => f.Key, f => ((long)f.Value.Length, 0L));
		var hashes = Build.ToDictionary(f => f.Key, f => Convert.ToHexStringLower(SHA256.HashData(f.Value)));
		List<string> mapped = [.. Build.Keys];

		Assert.Equal(["нет на Deck: " + IndexPath], layout.Compare(files, mapped, hashes));

		files[IndexPath] = (index.Length, 0L);
		hashes[IndexPath] = new string('0', 64);
		Assert.Equal([IndexPath + " на Deck не тот, что в сборке на ПК"], layout.Compare(files, mapped, hashes));

		files["CarX_Street_Data/StreamingAssets/aa/Old/player_layout.bundle"] = (1, 0L);
		Assert.Equal(["несколько player_layout.bundle в CarX_Street_Data/StreamingAssets/aa/"], layout.Compare(files, mapped, hashes));
	}

	[Fact]
	public void LoadsIndexFromBuildFolder()
	{
		string build = Directory.CreateTempSubdirectory("sdv-layout-").FullName;
		try
		{
			Assert.Null(PlayerLayout.Load(build));

			string path = Path.Combine(build, IndexPath.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllBytes(path, WriteIndex(Build));
			Assert.Equal(IndexPath, PlayerLayout.Load(build)!.IndexPath);

			string second = Path.Combine(build, "CarX_Street_Data", "StreamingAssets", "aa", "Old", "player_layout.bundle");
			Directory.CreateDirectory(Path.GetDirectoryName(second)!);
			File.WriteAllBytes(second, WriteIndex(Build));
			Assert.Throws<InvalidDataException>(() => PlayerLayout.Load(build));
		}
		finally
		{
			Directory.Delete(build, recursive: true);
		}
	}

	[Fact]
	public void ParsesHeaderProbeOutput()
	{
		Assert.Equal(["a/elf", "run me"], PlayerLayout.ParseExecutables("7f454c46\ta/elf\n554e4954\tlevel0\n23212f62\trun me\n6162\tshort\n"));
	}
}

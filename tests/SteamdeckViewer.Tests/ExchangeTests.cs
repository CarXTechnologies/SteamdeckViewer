using SteamdeckViewer.Core;

namespace SteamdeckViewer.Tests;

public sealed class ExchangeTests
{
	private static Dictionary<string, ExchangeFile> Files(params ExchangeFile[] files)
	{
		return files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
	}

	// Файл уходит, когда не менялся между двумя проходами и в таком виде ещё не передавался
	[Fact]
	public void ReadyWhenStableAndNotSent()
	{
		var photo = new ExchangeFile("photo.png", 100, 1000);
		var growing = new ExchangeFile("video.mp4", 500, 1000);
		var sent = new ExchangeFile("old.txt", 10, 900);

		// Первый проход: сравнивать не с чем — ждём
		Assert.Empty(ExchangeFolders.Ready(Files(photo), null, Files()));

		List<ExchangeFile> ready = ExchangeFolders.Ready(
			Files(photo, growing with { Size = 800 }, sent),
			Files(photo, growing, sent),
			Files(sent));
		Assert.Equal([photo], ready);

		// Уже переданный файл изменился — уходит снова
		var edited = sent with { Size = 12, MTime = 950 };
		Assert.Equal([edited], ExchangeFolders.Ready(Files(edited), Files(edited), Files(sent)));
	}

	[Theory]
	[InlineData("photo.png", "photo.png")]
	[InlineData("Скриншоты/2026-10-05 12:00.png", "Скриншоты\\2026-10-05 12_00.png")]
	[InlineData("a<b>c\"d|e?f*g.txt", "a_b_c_d_e_f_g.txt")]
	[InlineData("dots... /name. ", "dots\\name")]
	[InlineData("CON.txt", "_CON.txt")]
	[InlineData("logs/aux", "logs\\_aux")]
	[InlineData("COM1.log", "_COM1.log")]
	[InlineData("COMX.log", "COMX.log")]
	public void LinuxNamesBecomeValidOnWindows(string linux, string windows)
	{
		Assert.Equal(windows.Replace('\\', Path.DirectorySeparatorChar), ExchangeFolders.ToWindowsPath(linux));
	}

	[Theory]
	[InlineData(".directory", true)]
	[InlineData("Скриншоты/.directory", true)]
	[InlineData("film.mkv.part", true)]
	[InlineData("~$report.docx", true)]
	[InlineData(".~lock.report.odt#", true)]
	[InlineData("notes.txt~", true)]
	[InlineData("desktop.ini", true)]
	[InlineData("photo.png.sdv-part", true)]
	[InlineData("photo.png", false)]
	[InlineData("build/CarX_Street.x86_64", false)]
	public void JunkIsNotTransferred(string path, bool junk)
	{
		Assert.Equal(junk, ExchangeFolders.Junk.IsExcluded(path));
	}

	[Fact]
	public void ManifestPathPerDevice()
	{
		string byKey = ExchangeFolders.DefaultManifestPath(new DeckDevice { Host = "10.23.3.120", User = "deck", HostKeyFingerprint = "SHA256:ab/c+d=" });
		string byHost = ExchangeFolders.DefaultManifestPath(new DeckDevice { Host = "10.23.3.120", User = "deck" });

		Assert.Equal("SHA256_ab_c_d_.json", Path.GetFileName(byKey));
		Assert.Equal("deck@10.23.3.120.json", Path.GetFileName(byHost));
		Assert.EndsWith(Path.Combine("SteamdeckViewer", "exchange"), Path.GetDirectoryName(byHost));
	}

	// Папки на рабочем столе Deck: без xdg-user-dir (и когда он отдаёт $HOME) — ~/Desktop
	[WslFact]
	public void PrepareScriptCreatesDesktopFolders()
	{
		string home = Wsl.NewHome();
		CommandResult result = Wsl.Run(
			"mkdir -p ~/bin; printf '#!/bin/sh\\necho \"$HOME\"\\n' > ~/bin/xdg-user-dir; chmod +x ~/bin/xdg-user-dir; export PATH=\"$HOME/bin:$PATH\"\n" +
			Wsl.Subshell(ExchangeFolders.PrepareScript) + "\necho \"EXIT=$?\"; ls ~/Desktop", home);

		Assert.Contains("EXIT=0", result.Output);
		Assert.StartsWith(home + "/Desktop\n", result.Output);
		Assert.Contains(ExchangeFolders.DeckOutbox, result.Output);
		Assert.Contains(ExchangeFolders.DeckInbox, result.Output);
	}
}

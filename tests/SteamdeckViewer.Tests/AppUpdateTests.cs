using SteamdeckViewer.Core;

namespace SteamdeckViewer.Tests;

public sealed class AppUpdateTests
{
	[Fact]
	public void ConvertsShareLinksToText()
	{
		Assert.Equal("https://docs.google.com/document/d/1AbC-d_9/export?format=txt",
			AppUpdate.ToTextUrl("https://docs.google.com/document/d/1AbC-d_9/edit?usp=sharing"));
		Assert.Equal("https://drive.google.com/uc?export=download&id=1XyZ",
			AppUpdate.ToTextUrl(" https://drive.google.com/file/d/1XyZ/view?usp=drive_link "));
		Assert.Equal("https://intranet.local/deck-tools.txt", AppUpdate.ToTextUrl("https://intranet.local/deck-tools.txt"));
	}

	// Выгрузка Google Doc в txt: BOM в начале, переводы строк \r\n
	[Fact]
	public void ParsesNotice()
	{
		UpdateNotice? notice = AppUpdate.Parse("\uFEFFversion: 1.0.1\r\nurl: https://drive.google.com/file/d/1XyZ/view\r\nmessage: Поиск в Player.log\r\nи скриншот Deck\r\n");

		Assert.NotNull(notice);
		Assert.Equal(new Version(1, 0, 1, 0), notice.Version);
		Assert.Equal("https://drive.google.com/file/d/1XyZ/view", notice.Url);
		Assert.Equal("Поиск в Player.log\nи скриншот Deck", notice.Message);

		Assert.Equal("", AppUpdate.Parse("Версия = 2\nСсылка = https://x.local/a.exe")!.Message);
	}

	[Theory]
	[InlineData("<!DOCTYPE html><html><head><title>Google Docs: вход</title>")]
	[InlineData("version: 1.0.1")]
	[InlineData("version: 1.0.1\nurl: file:///c:/a.exe")]
	[InlineData("version: один\nurl: https://x.local/a.exe")]
	public void IgnoresWhatIsNotANotice(string text)
	{
		Assert.Null(AppUpdate.Parse(text));
	}

	[Theory]
	[InlineData("1.0.1", "1.0.0", true)]
	[InlineData("1.0", "1.0.0", false)]
	[InlineData("1.0.0", "1.0.0", false)]
	[InlineData("1.2", "1.10", false)]
	[InlineData("v2", "1.9.9", true)]
	public void ComparesVersions(string published, string current, bool newer)
	{
		var notice = new UpdateNotice(AppUpdate.ParseVersion(published)!, "https://x.local", "");
		Assert.Equal(newer, AppUpdate.IsNewer(notice, current));
	}
}

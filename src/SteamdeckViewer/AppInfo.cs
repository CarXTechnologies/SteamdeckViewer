namespace SteamdeckViewer;

// Имя и версия программы для заголовка окна. Версию меняет пункт «Версия программы» в publish.bat
internal static class AppInfo
{
	public const string Name = "CarX Deck Tools";
	public const string AppVersionText = "1.0.0";

	public const string DocsUrl = "https://carxtehnologies.atlassian.net/wiki/spaces/CXSPC/pages/5520719924";
	public const string DownloadUrl = "https://carxtehnologies.atlassian.net/wiki/download/attachments/5520719924/CarXDeckTools.zip?api=v2";

	public static string Title => $"{Name} {AppVersionText}";
}

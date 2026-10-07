namespace SteamdeckViewer;

// Имя и версия программы для заголовка окна. Версию меняет пункт «Версия программы» в publish.bat
internal static class AppInfo
{
	public const string Name = "CarX Deck Tools";
	public const string AppVersionText = "1.0.0";

	public static string Title => $"{Name} {AppVersionText}";
}

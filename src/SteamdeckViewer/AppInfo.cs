namespace SteamdeckViewer;

// Имя и версия программы для заголовка окна. Версию меняет пункт «Версия программы» в publish.bat
internal static class AppInfo
{
	public const string Name = "CarX Deck Tools";
	public const string AppVersionText = "1.0.0";

	public const string UpdateUrl = "https://docs.google.com/document/d/1oBXpuAe1eUch8ot4600eoiQ4whCeu-VbxynWsr9kvFo/edit?usp=sharing";

	public static string Title => $"{Name} {AppVersionText}";
}

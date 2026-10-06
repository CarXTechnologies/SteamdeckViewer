namespace SteamdeckViewer;

// Журнал непредвиденных ошибок: %APPDATA%\SteamdeckViewer\errors.log
internal static class ErrorLog
{
	public static readonly string FilePath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamdeckViewer", "errors.log");

	private static readonly Lock s_lock = new();

	public static void Write(Exception e)
	{
		try
		{
			lock (s_lock)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
				File.AppendAllText(FilePath, $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{e}\n\n");
			}
		}
		catch
		{
			// журнал — не повод для ещё одной ошибки
		}
	}
}

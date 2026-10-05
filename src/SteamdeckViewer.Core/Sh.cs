namespace SteamdeckViewer.Core;

// Экранирование для POSIX-шелла на Deck: всё, что подставляется в команды, проходит через эти методы
public static class Sh
{
	public static string Quote(string value)
	{
		return "'" + value.Replace("'", "'\\''") + "'";
	}

	// "~/x y" -> "$HOME"/'x y': тильда внутри кавычек не раскрывается, поэтому домашнюю папку подставляем отдельно
	public static string Path(string path)
	{
		if (path == "~")
		{
			return "\"$HOME\"";
		}

		if (path.StartsWith("~/", StringComparison.Ordinal))
		{
			return "\"$HOME\"/" + Quote(path[2..]);
		}

		return Quote(path);
	}

	public static string JoinPath(string folder, string name)
	{
		return folder.EndsWith('/') ? folder + name : folder + "/" + name;
	}

	public static string ParentPath(string path)
	{
		string trimmed = path.TrimEnd('/');
		int slash = trimmed.LastIndexOf('/');
		return slash switch
		{
			< 0 => path,
			0 => "/",
			_ => trimmed[..slash]
		};
	}
}

namespace SteamdeckViewer.Core;

// Экранирование для POSIX-шелла на Deck: всё, что подставляется в команды, проходит через эти методы
public static class Sh
{
	public static string Quote(string value)
	{
		return "'" + value.Replace("'", "'\\''") + "'";
	}

	// Долгая команда (заливка), во время которой Deck не должен уснуть. Блокировку сна из сеанса SSH logind даёт только
	// по правилу polkit (его ставит установка Sunshine), блокировку простоя — всем. Если systemd-inhibit не разрешил
	// ни ту ни другую или его нет, команда выполняется как есть: без блокировки, но и без отказа
	public static string KeepAwake(string why, string command)
	{
		return
			"sdv_what=; for w in sleep:idle idle; do " +
			"if systemd-inhibit --what=$w --who='CarX Deck Tools' --why=check true >/dev/null 2>&1; then sdv_what=$w; break; fi; done; " +
			$"if [ -n \"$sdv_what\" ]; then systemd-inhibit --what=$sdv_what --who='CarX Deck Tools' --why={Quote(why)} --mode=block {command}; " +
			$"else {command}; fi";
	}

	// Многострочные скрипты в исходниках с CRLF (git с core.autocrlf) несут \r в конце строк, а для bash «do\r» и «fi\r» —
	// не ключевые слова. Всё, что уходит на Deck как команда, проходит через это
	public static string UnixNewlines(string script)
	{
		return script.Replace("\r\n", "\n");
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

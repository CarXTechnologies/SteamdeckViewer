using System.Text.RegularExpressions;

namespace SteamdeckViewer.Core;

public enum LogLevel
{
	Info,
	Warning,
	Error
}

// Запись Player.log: сообщение со стеком вызовов или одиночная строка. Уровень записи — самый высокий из её строк:
// у Debug.LogWarning/LogError тип виден только по кадру UnityEngine.Debug:LogWarning(...) в стеке
public sealed class LogEntry
{
	internal LogEntry(LogLine first)
	{
		First = first;
	}

	public LogLine First { get; internal set; }
	public LogLevel Level { get; internal set; }

	internal void Add(LogLine line)
	{
		line.Entry = this;
		if (line.OwnLevel > Level)
		{
			Level = line.OwnLevel;
		}
	}
}

public sealed class LogLine
{
	internal LogLine(string text)
	{
		Text = text;
	}

	public string Text { get; }

	// null — пустая строка между записями
	public LogEntry? Entry { get; internal set; }

	// Кадр стека вызовов
	public bool IsFrame { get; internal set; }

	// Сообщение самой программы, а не игры
	public bool IsTool { get; internal set; }

	// Вытеснена из начала лога (PlayerLogModel.MaxLines)
	public bool IsRemoved { get; internal set; }

	internal LogLevel OwnLevel { get; set; }

	public LogLevel Level => Entry?.Level ?? LogLevel.Info;
	public bool StartsEntry => Entry?.First == this;
}

// Строки Player.log, разбитые на записи. Unity пишет управляемое сообщение так: текст (бывает в несколько строк),
// стек вызовов, пустая строка. Нативные строки (gamescope, Vulkan, «UnloadTime: …») идут без стека и без пустой
// строки, поэтому начало сообщения со стеком не размечено: сообщением считаются до MaxMessageLines строк перед стеком
public sealed class PlayerLogModel(int maxLines)
{
	public const string ToolPrefix = "[CarX Deck Tools]";
	private const int MaxMessageLines = 10;

	// Управляемый кадр Unity «Namespace.Type:Method(Args) (at path:line)», кадр исключения Mono «  at …»,
	// кадр нативного падения «#0 0x…»
	private static readonly Regex Frame = new(@"^(?:\s+at |#\d+\s+0x|[\w.<>`$\[\],+|-]+:[\w.<>`$|,\[\]-]+ ?\()", RegexOptions.CultureInvariant);

	// Заголовок необработанного исключения: «NullReferenceException: …», «Rethrow as InvalidOperationException: …»
	private static readonly Regex ExceptionHeader = new(@"^(?:Rethrow as )?[\w.`+]*Exception(?:: |$)", RegexOptions.CultureInvariant);

	private static readonly string[] ErrorMarkers =
	[
		"UnityEngine.Debug:LogError", "UnityEngine.Debug:LogException", "UnityEngine.Debug:LogAssertion", "UnityEngine.Debug:Assert",
		"Caught fatal signal", "Crash!!!", "Segmentation fault"
	];

	private readonly List<LogLine> m_lines = [];

	// Строки после последней границы записи (пустой строки, стека, сообщения программы) — кандидаты в сообщение перед стеком
	private readonly List<LogLine> m_message = [];
	private LogEntry? m_trace;

	public int MaxLines { get; } = maxLines;
	public IReadOnlyList<LogLine> Lines => m_lines;

	// Добавляет строку или несколько через \n; продолжение многострочного сообщения программы — тоже от программы
	public void Add(string text)
	{
		string[] parts = text.Split('\n');
		bool tool = parts[0].StartsWith(ToolPrefix, StringComparison.Ordinal);
		foreach (string part in parts)
		{
			// \r в середине строки разбил бы её на две в окне лога
			Add(part.TrimEnd('\r').Replace('\r', ' '), tool);
		}

		if (m_lines.Count > MaxLines)
		{
			int excess = m_lines.Count - MaxLines;
			for (int i = 0; i < excess; i++)
			{
				m_lines[i].IsRemoved = true;
			}

			m_lines.RemoveRange(0, excess);
		}
	}

	public void Clear()
	{
		foreach (LogLine line in m_lines)
		{
			line.IsRemoved = true;
		}

		m_lines.Clear();
		Boundary();
	}

	// Сколько записей такого уровня в логе
	public int Count(LogLevel level)
	{
		int count = 0;
		foreach (LogLine line in m_lines)
		{
			if (line.StartsEntry && line.Level == level && !line.IsTool)
			{
				count++;
			}
		}

		return count;
	}

	// Строки записей не ниже уровня и пустые строки после них. Info — весь лог
	public List<LogLine> Filter(LogLevel minimum)
	{
		if (minimum == LogLevel.Info)
		{
			return [.. m_lines];
		}

		var result = new List<LogLine>();
		bool previousShown = false;
		foreach (LogLine line in m_lines)
		{
			bool show = line.Entry == null ? previousShown : line.Level >= minimum && !line.IsTool;
			if (show)
			{
				result.Add(line);
			}

			previousShown = show && line.Entry != null;
		}

		return result;
	}

	public static LogLevel LevelOf(string line)
	{
		foreach (string marker in ErrorMarkers)
		{
			if (line.Contains(marker, StringComparison.Ordinal))
			{
				return LogLevel.Error;
			}
		}

		if (line.Contains("UnityEngine.Debug:LogWarning", StringComparison.Ordinal))
		{
			return LogLevel.Warning;
		}

		if (ExceptionHeader.IsMatch(line))
		{
			return LogLevel.Error;
		}

		// Steam подкладывает 32-битный gameoverlayrenderer.so в 64-битную игру при каждом запуске: загрузчик пишет
		// «ERROR: ld.so: … cannot be preloaded … ignored.», на игру это не влияет
		if (line.StartsWith("ERROR: ld.so:", StringComparison.Ordinal) && line.EndsWith("ignored.", StringComparison.Ordinal))
		{
			return LogLevel.Warning;
		}

		if (line.StartsWith("ERROR:", StringComparison.Ordinal) || line.StartsWith("FATAL:", StringComparison.Ordinal) ||
		    line.Contains("[error]", StringComparison.OrdinalIgnoreCase) || line.Contains("[fatal]", StringComparison.OrdinalIgnoreCase))
		{
			return LogLevel.Error;
		}

		if (line.StartsWith("WARNING:", StringComparison.Ordinal) ||
		    line.Contains("[warning]", StringComparison.OrdinalIgnoreCase) || line.Contains("[warn]", StringComparison.OrdinalIgnoreCase))
		{
			return LogLevel.Warning;
		}

		return LogLevel.Info;
	}

	public static bool IsFrameLine(string line)
	{
		return Frame.IsMatch(line);
	}

	// Начала совпадений поиска в строке без учёта регистра, без перекрытий. Пустой запрос не совпадает ни с чем:
	// IndexOf("") находит пустую строку на том же месте, и перебор не двигался бы дальше
	public static IEnumerable<int> Matches(string text, string query)
	{
		if (query.Length == 0)
		{
			yield break;
		}

		for (int i = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(query, i + query.Length, StringComparison.OrdinalIgnoreCase))
		{
			yield return i;
		}
	}

	private void Add(string text, bool tool)
	{
		var line = new LogLine(text) { IsTool = tool };
		m_lines.Add(line);

		if (tool)
		{
			new LogEntry(line).Add(line);
			Boundary();
			return;
		}

		if (string.IsNullOrWhiteSpace(text))
		{
			Boundary();
			return;
		}

		line.IsFrame = IsFrameLine(text);
		line.OwnLevel = LevelOf(text);

		// До пустой строки всё идёт в запись со стеком: «Rethrow as …» и кадры внутреннего исключения
		if (m_trace != null)
		{
			m_trace.Add(line);
			return;
		}

		if (line.IsFrame)
		{
			m_trace = new LogEntry(m_message.Count > 0 ? m_message[0] : line);
			foreach (LogLine message in m_message)
			{
				m_trace.Add(message);
			}

			m_trace.Add(line);
			m_message.Clear();
			return;
		}

		new LogEntry(line).Add(line);
		m_message.Add(line);
		if (m_message.Count > MaxMessageLines)
		{
			m_message.RemoveAt(0);
		}
	}

	private void Boundary()
	{
		m_trace = null;
		m_message.Clear();
	}
}

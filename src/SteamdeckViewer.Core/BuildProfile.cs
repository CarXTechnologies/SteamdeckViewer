using System.Text;
using System.Text.Json.Serialization;

namespace SteamdeckViewer.Core;

public enum DeckRuntime
{
	Native,
	SteamLinuxRuntime3,
	Proton
}

public enum LaunchMode
{
	// Ярлык «Devkit Game: …» в библиотеке Steam: игра получает фокус gamescope, Steam Input и оверлей
	Steam,
	// systemd-run от пользователя deck, без Steam — удобно в режиме рабочего стола
	Direct
}

// Профиль тестового билда: откуда заливать, как запускать и где на Deck искать Player.log.
// Значения по умолчанию — под Linux-билд CarX Street
public sealed class BuildProfile
{
	public string Name { get; set; } = "CarX Street";
	public string LocalFolder { get; set; } = string.Empty;
	public string Executable { get; set; } = "CarX_Street.x86_64";
	public string Arguments { get; set; } = string.Empty;

	// Игра узнаёт Deck по SteamDeck=1 (SteamDeckExtensions); Steam выставляет его сам, при прямом запуске — нет
	public string EnvironmentVariables { get; set; } = "SteamDeck=1";

	public DeckRuntime Runtime { get; set; } = DeckRuntime.Native;
	public LaunchMode LaunchMode { get; set; } = LaunchMode.Steam;

	// Отладочные символы IL2CPP и Burst на Deck не нужны
	public string Excludes { get; set; } = "*_BackUpThisFolder_ButDontShipItWithYourGame/ *_BurstDebugInformation_DoNotShip/";

	// Проверка целостности CarX Street (player_layout.bundle) падает с E29 на любом лишнем .so/.dll в папке игры
	public bool DeleteExtraneous { get; set; } = true;
	public bool StopBeforeUpload { get; set; } = true;

	// Сверка файлов на Deck с player_layout.bundle (PlayerLayout) — логика проверки целостности CarX Street PC,
	// у других проектов такого индекса нет, поэтому по умолчанию выключена
	public bool CheckPlayerLayout { get; set; }
	public string SteamAppId { get; set; } = string.Empty;
	public string PlayerLogPath { get; set; } = "~/.config/unity3d/CarX Technologies/CarX Street/Player.log";

	// Идёт в URL команды Steam без экранирования. Steam принимает только id по шаблону devkit-клиента Valve
	// ^[A-Za-z_][A-Za-z0-9_.]+$, иначе create-shortcut отвечает «missing/invalid arguments»
	[JsonIgnore]
	public string GameId
	{
		get
		{
			var id = new StringBuilder();
			foreach (char c in Name.Trim())
			{
				id.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' ? c : '_');
			}

			if (id.Length == 0)
			{
				return "game";
			}

			if (!char.IsAsciiLetter(id[0]) && id[0] != '_')
			{
				id.Insert(0, '_');
			}

			return id.Length < 2 ? id.Append('_').ToString() : id.ToString();
		}
	}

	// id до 2026-10-06: с дефисами вместо пробелов. Залитую под ним папку переносим, а не заливаем заново
	[JsonIgnore]
	internal string LegacyGameId
	{
		get
		{
			var id = new StringBuilder();
			foreach (char c in Name.Trim())
			{
				id.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
			}

			return id.Length > 0 ? id.ToString() : "game";
		}
	}

	[JsonIgnore]
	public string RemoteFolder => DevkitGames.GamesRoot + "/" + GameId;

	[JsonIgnore]
	public string StartCommand
	{
		get
		{
			string exe = Executable.Trim().Replace('\\', '/');
			if (exe.Contains(' '))
			{
				exe = "\"" + exe + "\"";
			}

			return string.IsNullOrWhiteSpace(Arguments) ? exe : exe + " " + Arguments.Trim();
		}
	}

	public IReadOnlyDictionary<string, string> ParseEnvironment()
	{
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (string token in EnvironmentVariables.Split([' ', '\n', '\r', '\t', ';'], StringSplitOptions.RemoveEmptyEntries))
		{
			int eq = token.IndexOf('=');
			if (eq > 0)
			{
				result[token[..eq]] = token[(eq + 1)..];
			}
		}

		return result;
	}

	public override string ToString()
	{
		return Name;
	}
}

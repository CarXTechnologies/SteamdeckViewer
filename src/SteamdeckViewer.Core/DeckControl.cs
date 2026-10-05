using Renci.SshNet.Common;

namespace SteamdeckViewer.Core;

public enum DeckSession
{
	GameMode,
	Desktop
}

// Питание, режимы и системные переключатели Deck
public static class DeckControl
{
	private const string PolkitHelpers = "/usr/bin/steamos-polkit-helpers";

	// SteamOS 3.9+ переименовал скрипт в holo-session-select — так же выбирает и клиент Valve
	public static Task<CommandResult> SwitchSessionAsync(DeckConnection deck, DeckSession session, CancellationToken ct)
	{
		string arg = session == DeckSession.GameMode ? "gamescope" : "plasma";
		return deck.RunAsync(
			$"if command -v holo-session-select >/dev/null 2>&1; then holo-session-select {arg}; else steamos-session-select {arg}; fi",
			ct);
	}

	// Перезапуск Game Mode вместе со Steam (так «Restart Session» делает клиент Valve)
	public static Task<CommandResult> RestartSteamAsync(DeckConnection deck, CancellationToken ct)
	{
		return SwitchSessionAsync(deck, DeckSession.GameMode, ct);
	}

	// polkit-хелперы SteamOS работают без пароля sudo; если их нет — обычный systemctl
	public static Task<CommandResult> RebootAsync(DeckConnection deck, CancellationToken ct)
	{
		return RunPowerCommandAsync(deck,
			$"if [ -x {PolkitHelpers}/steamos-reboot-now ]; then {PolkitHelpers}/steamos-reboot-now; else systemctl reboot; fi", ct);
	}

	public static Task<CommandResult> PowerOffAsync(DeckConnection deck, CancellationToken ct)
	{
		return RunPowerCommandAsync(deck,
			$"if [ -x {PolkitHelpers}/steamos-poweroff-now ]; then {PolkitHelpers}/steamos-poweroff-now; else systemctl poweroff; fi", ct);
	}

	// Steam включает удалённую отладку CEF (порт 8081) после перезапуска, если есть этот файл
	public static Task<CommandResult> EnableCefDebuggingAsync(DeckConnection deck, CancellationToken ct)
	{
		return deck.RunAsync("touch ~/.steam/steam/.cef-enable-remote-debugging", ct);
	}

	// Разрыв соединения во время перезагрузки — ожидаемый исход
	private static async Task<CommandResult> RunPowerCommandAsync(DeckConnection deck, string command, CancellationToken ct)
	{
		try
		{
			return await deck.RunAsync(command, ct);
		}
		catch (Exception e) when (e is SshConnectionException or SshException or ObjectDisposedException)
		{
			return new CommandResult(0, string.Empty, string.Empty);
		}
	}
}

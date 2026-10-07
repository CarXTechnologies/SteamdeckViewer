namespace SteamdeckViewer.Core;

// Снимок экрана Deck без Moonlight. В Game Mode снимает сам gamescope (gamescopectl screenshot): игра вместе
// с оверлеями (MangoHud, Steam) и интерфейсом Steam — как на экране. На рабочем столе — spectacle через KWin.
// Файл пишется во временный на Deck, скачивается и удаляется
public static class DeckScreenshot
{
	private const int NoSession = 2;
	private const int GamescopeFailed = 3;
	private const int NoFile = 4;

	public static string FileName(DateTime now)
	{
		return $"SteamDeck_{now:yyyy-MM-dd_HH-mm-ss}.png";
	}

	// spectacle — из менеджера systemd пользователя: у него окружение графического сеанса (WAYLAND_DISPLAY и шина)
	internal static string Script(string remotePath)
	{
		return $$"""
			f={{Sh.Quote(remotePath)}}
			rm -f "$f"
			export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
			export DBUS_SESSION_BUS_ADDRESS="${DBUS_SESSION_BUS_ADDRESS:-unix:path=$XDG_RUNTIME_DIR/bus}"
			if pgrep -x 'gamescope(-wl)?' >/dev/null 2>&1; then
				gamescopectl screenshot "$f" >/dev/null 2>&1 || exit {{GamescopeFailed}}
			elif pgrep -x 'kwin_wayland|kwin_x11|plasmashell' >/dev/null 2>&1; then
				systemd-run --user --wait --collect --quiet -p RuntimeMaxSec=20 spectacle --background --nonotify --fullscreen --output "$f" >/dev/null 2>&1
			else
				exit {{NoSession}}
			fi
			# gamescope пишет файл после следующего кадра: ждём, пока он появится и перестанет расти
			prev=-1
			for i in $(seq 1 50); do
				size=$(stat -c %s "$f" 2>/dev/null || echo 0)
				if [ "$size" -gt 0 ] && [ "$size" = "$prev" ]; then exit 0; fi
				prev=$size
				sleep 0.1
			done
			exit {{NoFile}}
			""";
	}

	public static async Task TakeAsync(DeckConnection deck, string localPath, CancellationToken ct)
	{
		string remote = $"/tmp/carx-deck-tools-{Guid.NewGuid():N}.png";
		try
		{
			CommandResult result = await deck.RunAsync(Script(remote), ct);
			if (!result.Success)
			{
				throw new InvalidOperationException(result.ExitCode switch
				{
					NoSession => "Скриншот не сделан: на Deck не запущены ни Game Mode, ни рабочий стол (экран входа или Deck спит).",
					GamescopeFailed => "Скриншот не сделан: gamescopectl не ответил.",
					NoFile => "Скриншот не сделан: файл снимка не появился за 5 с.",
					_ => "Скриншот не сделан: " + result.Combined.Trim()
				});
			}

			try
			{
				await using FileStream output = File.Create(localPath);
				deck.GetSftp().DownloadFile(remote, output);
			}
			catch
			{
				// Недокачанный снимок на ПК не нужен
				File.Delete(localPath);
				throw;
			}
		}
		finally
		{
			try
			{
				await deck.RunAsync("rm -f " + Sh.Quote(remote), CancellationToken.None);
			}
			catch
			{
				// Связь оборвалась — в /tmp файл всё равно сотрётся при перезагрузке
			}
		}
	}
}

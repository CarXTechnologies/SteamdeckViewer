using System.Text;

namespace SteamdeckViewer.Core;

public sealed class SudoPasswordException() : Exception("Неверный пароль sudo.");

// Разовые действия от root: скрипт кладётся во временный файл (только для владельца),
// пароль уходит в stdin `sudo -S` и нигде не сохраняется — ни на Deck, ни у нас
public static class DeckSudo
{
	public static async Task<CommandResult> RunScriptAsync(DeckConnection deck, string password, string script, CancellationToken ct)
	{
		string path = "/tmp/sdv-root-" + Guid.NewGuid().ToString("N") + ".sh";
		byte[] scriptBytes = Encoding.UTF8.GetBytes(Sh.UnixNewlines(script));

		CommandResult written = await deck.RunWithInputAsync(WriteCommand(path),
			async (input, token) => await input.WriteAsync(scriptBytes, token), ct);
		if (!written.Success)
		{
			throw new InvalidOperationException("Не удалось подготовить скрипт: " + written.Combined.Trim());
		}

		byte[] passwordBytes = Encoding.UTF8.GetBytes(password + "\n");
		CommandResult result = await deck.RunWithInputAsync(RunCommand(path),
			async (input, token) => await input.WriteAsync(passwordBytes, token), ct);

		if (!result.Success && IsWrongPassword(result.Error))
		{
			throw new SudoPasswordException();
		}

		return result;
	}

	internal static string WriteCommand(string path)
	{
		return $"umask 077; cat > {path}";
	}

	// Скрипт удаляется в любом исходе, код возврата сохраняется
	internal static string RunCommand(string path)
	{
		return $"sudo -S -p '' bash {path}; code=$?; rm -f {path}; exit $code";
	}

	internal static bool IsWrongPassword(string stderr)
	{
		return stderr.Contains("incorrect password", StringComparison.OrdinalIgnoreCase) ||
		       stderr.Contains("Sorry, try again", StringComparison.OrdinalIgnoreCase) ||
		       stderr.Contains("no password was provided", StringComparison.OrdinalIgnoreCase);
	}
}

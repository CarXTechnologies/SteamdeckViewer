using System.Diagnostics;
using System.Text;
using SteamdeckViewer.Core;

namespace SteamdeckViewer.Tests;

// Скрипты для Deck прогоняются в настоящем Linux через WSL: bash, GNU tar/find/xargs, /proc — как на SteamOS.
// HOME подменяется временной папкой внутри Linux, поэтому ~/devkit-game и ~/.steam не трогают ничего настоящего
internal static class Wsl
{
	public static readonly bool Available = Check();

	public static string NewHome()
	{
		return "/tmp/sdv-test-" + Guid.NewGuid().ToString("N");
	}

	public static string ToLinuxPath(string windowsPath)
	{
		string full = Path.GetFullPath(windowsPath);
		return "/mnt/" + char.ToLowerInvariant(full[0]) + full[2..].Replace('\\', '/');
	}

	public static CommandResult Run(string script, string? home = null, int timeoutMs = 60000)
	{
		var psi = new ProcessStartInfo("wsl.exe", "-e bash -s")
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};

		var full = new StringBuilder();
		if (home != null)
		{
			full.Append($"export HOME={Sh.Quote(home)}\nmkdir -p \"$HOME\"\ncd \"$HOME\"\n");
		}
		full.Append(script.Replace("\r\n", "\n"));
		full.Append('\n');

		using Process process = Process.Start(psi)!;
		using (var input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { NewLine = "\n" })
		{
			input.Write(full.ToString());
		}

		Task<string> output = process.StandardOutput.ReadToEndAsync();
		Task<string> error = process.StandardError.ReadToEndAsync();
		if (!process.WaitForExit(timeoutMs))
		{
			process.Kill(entireProcessTree: true);
			throw new TimeoutException("WSL-скрипт не завершился вовремя:\n" + script);
		}

		return new CommandResult(process.ExitCode, output.Result, error.Result);
	}

	// Скрипт целиком в подоболочке: его exit не обрывает проверочный код после него
	public static string Subshell(string script)
	{
		return "\n(\n" + script + "\n)";
	}

	private static bool Check()
	{
		try
		{
			return OperatingSystem.IsWindows() && Run("echo ok", timeoutMs: 30000).Output.Trim() == "ok";
		}
		catch
		{
			return false;
		}
	}
}

public sealed class WslFactAttribute : FactAttribute
{
	public WslFactAttribute()
	{
		if (!Wsl.Available)
		{
			Skip = "WSL недоступен";
		}
	}
}

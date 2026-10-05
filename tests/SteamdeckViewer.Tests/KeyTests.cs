using System.Diagnostics;
using System.Security.Cryptography;
using SteamdeckViewer.Core;

namespace SteamdeckViewer.Tests;

public sealed class KeyTests
{
	private static readonly string SshKeygen = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe");

	// ssh-keygen из Windows читает наш приватный ключ (значит, права на файл его устраивают)
	// и выводит из него ровно ту же публичную часть, что пишем мы
	[Fact]
	public void PublicKeyMatchesOpenSsh()
	{
		if (!File.Exists(SshKeygen))
		{
			return;
		}

		string folder = Directory.CreateTempSubdirectory("sdv-key-").FullName;
		string keyPath = Path.Combine(folder, "deck_rsa");
		try
		{
			using RSA rsa = RSA.Create(2048);
			File.WriteAllText(keyPath, rsa.ExportRSAPrivateKeyPem() + "\n");
			DeckKeys.RestrictToCurrentUser(keyPath);
			string ours = DeckKeys.PublicKeyLine(rsa, "test@host");

			string derived = Run(SshKeygen, $"-y -f \"{keyPath}\"");

			Assert.Equal(string.Join(' ', ours.Split(' ')[..2]), string.Join(' ', derived.Trim().Split(' ')[..2]));
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	private static string Run(string exe, string args)
	{
		using Process process = Process.Start(new ProcessStartInfo(exe, args)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		})!;

		string output = process.StandardOutput.ReadToEnd();
		string error = process.StandardError.ReadToEnd();
		process.WaitForExit();
		Assert.True(process.ExitCode == 0, error);
		return output;
	}
}

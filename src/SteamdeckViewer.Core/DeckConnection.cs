using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SteamdeckViewer.Core;

public sealed record CommandResult(int ExitCode, string Output, string Error)
{
	public bool Success => ExitCode == 0;

	public string Combined => string.IsNullOrWhiteSpace(Error) ? Output : (Output + Error);
}

public sealed class HostKeyMismatchException(string expected, string actual)
	: Exception($"Ключ хоста изменился.\nОжидался: {expected}\nПолучен:  {actual}")
{
	public string Actual { get; } = actual;
}

// Сервер не принял ни один ключ. «Too many authentication failures» приходит не как ошибка входа, а как разрыв соединения,
// поэтому оба случая сводятся сюда. ServerVersion — как представился сервер: по нему видно, SteamOS ли это вообще
public sealed class DeckAuthenticationException(string? serverVersion, Exception inner) : Exception(inner.Message, inner)
{
	public string? ServerVersion { get; } = serverVersion;

	// На SteamOS (Arch) OpenSSH не дописывает дистрибутив к версии, у Debian, Ubuntu и прочих он есть
	public bool IsNotSteamOs => ServerVersion != null &&
	                            (ServerVersion.Contains("Debian", StringComparison.OrdinalIgnoreCase) ||
	                             ServerVersion.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase) ||
	                             ServerVersion.Contains("Raspbian", StringComparison.OrdinalIgnoreCase) ||
	                             ServerVersion.Contains("FreeBSD", StringComparison.OrdinalIgnoreCase) ||
	                             ServerVersion.Contains("Windows", StringComparison.OrdinalIgnoreCase) ||
	                             !ServerVersion.Contains("OpenSSH", StringComparison.OrdinalIgnoreCase));
}

// Подключение к Deck по SSH: команды, потоки вывода, ввод в stdin и SFTP поверх отдельной сессии
public sealed class DeckConnection : IDisposable
{
	private readonly ConnectionInfo m_info;
	private readonly SshClient m_ssh;
	private readonly IReadOnlyList<PrivateKeyFile> m_keys;
	private readonly Lock m_sftpLock = new();
	private SftpClient? m_sftp;
	private bool m_hostKeyMismatch;

	public DeckDevice Device { get; }
	public string HostKeyFingerprint { get; private set; } = string.Empty;
	public string Home { get; private set; } = "/home/deck";
	public bool IsConnected => m_ssh.IsConnected;

	private DeckConnection(DeckDevice device, ConnectionInfo info, IReadOnlyList<PrivateKeyFile> keys)
	{
		Device = device;
		m_info = info;
		m_keys = keys;
		m_ssh = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(15) };
		m_ssh.HostKeyReceived += OnHostKeyReceived;
	}

	public static async Task<DeckConnection> ConnectAsync(DeckDevice device, CancellationToken ct)
	{
		var keys = new List<PrivateKeyFile>();
		foreach (string path in DeckKeys.AvailablePrivateKeys())
		{
			try
			{
				keys.Add(new PrivateKeyFile(path));
			}
			catch
			{
				// битый или недоступный ключ просто пропускаем
			}
		}

		if (keys.Count == 0)
		{
			throw new InvalidOperationException("Нет SSH-ключа. Сначала выполните сопряжение с Deck.");
		}

		var info = new ConnectionInfo(device.Host, device.SshPort, device.User,
			new PrivateKeyAuthenticationMethod(device.User, keys.ToArray<IPrivateKeySource>()))
		{
			Timeout = TimeSpan.FromSeconds(10)
		};

		var connection = new DeckConnection(device, info, keys);
		try
		{
			await connection.m_ssh.ConnectAsync(ct);
			CommandResult home = await connection.RunAsync("printf %s \"$HOME\"", ct);
			if (home.Success && home.Output.StartsWith('/'))
			{
				connection.Home = home.Output;
			}
		}
		catch (Exception) when (connection.m_hostKeyMismatch)
		{
			string actual = connection.HostKeyFingerprint;
			connection.Dispose();
			throw new HostKeyMismatchException(device.HostKeyFingerprint ?? string.Empty, actual);
		}
		catch (Exception e) when (e is SshAuthenticationException ||
		                          e is SshConnectionException && e.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase))
		{
			string? serverVersion = connection.m_ssh.ConnectionInfo?.ServerVersion;
			connection.Dispose();
			throw new DeckAuthenticationException(serverVersion, e);
		}
		catch
		{
			connection.Dispose();
			throw;
		}

		return connection;
	}

	// Запасной путь без Developer Mode: входим по паролю пользователя и дописываем наш ключ в authorized_keys
	public static async Task<string> InstallKeyWithPasswordAsync(DeckDevice device, string password, string publicKey, CancellationToken ct)
	{
		var keyboard = new KeyboardInteractiveAuthenticationMethod(device.User);
		keyboard.AuthenticationPrompt += (_, e) =>
		{
			foreach (AuthenticationPrompt prompt in e.Prompts)
			{
				prompt.Response = password;
			}
		};

		var info = new ConnectionInfo(device.Host, device.SshPort, device.User,
			new PasswordAuthenticationMethod(device.User, password), keyboard)
		{
			Timeout = TimeSpan.FromSeconds(10)
		};

		string fingerprint = string.Empty;
		using var client = new SshClient(info);
		client.HostKeyReceived += (_, e) =>
		{
			fingerprint = e.FingerPrintSHA256;
			e.CanTrust = device.HostKeyFingerprint == null || device.HostKeyFingerprint == fingerprint;
		};

		await client.ConnectAsync(ct);

		string key = Sh.Quote(publicKey.Trim());
		string script =
			"umask 077; mkdir -p ~/.ssh && touch ~/.ssh/authorized_keys && " +
			$"(grep -qxF {key} ~/.ssh/authorized_keys || printf '%s\\n' {key} >> ~/.ssh/authorized_keys) && " +
			"chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys";

		using SshCommand command = client.CreateCommand(Sh.UnixNewlines(script));
		await command.ExecuteAsync(ct);
		if (command.ExitStatus != 0)
		{
			throw new InvalidOperationException(command.Error.Trim());
		}

		client.Disconnect();
		return fingerprint;
	}

	public async Task<CommandResult> RunAsync(string command, CancellationToken ct = default)
	{
		using SshCommand cmd = m_ssh.CreateCommand(Sh.UnixNewlines(command));
		await cmd.ExecuteAsync(ct);
		return new CommandResult(cmd.ExitStatus ?? -1, cmd.Result, cmd.Error);
	}

	// Долгая команда (tail -F и т.п.): строки вывода отдаются по мере поступления, отмена посылает сигнал процессу
	public async Task<int> StreamLinesAsync(string command, Action<string> onLine, CancellationToken ct)
	{
		using SshCommand cmd = m_ssh.CreateCommand(Sh.UnixNewlines(command));
		Task execution = cmd.ExecuteAsync(ct);

		Task reading = Task.Run(async () =>
		{
			using var reader = new StreamReader(cmd.OutputStream, Encoding.UTF8);
			while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
			{
				onLine(line);
			}
		}, CancellationToken.None);

		try
		{
			await execution;
		}
		catch (OperationCanceledException)
		{
			// остановлено пользователем
		}

		await Task.WhenAny(reading, Task.Delay(2000, CancellationToken.None));
		return cmd.ExitStatus ?? -1;
	}

	// Команда с потоком в stdin (tar -x, xargs): writeInput пишет данные, по закрытию потока команда получает EOF
	public async Task<CommandResult> RunWithInputAsync(string command, Func<Stream, CancellationToken, Task> writeInput, CancellationToken ct)
	{
		using SshCommand cmd = m_ssh.CreateCommand(Sh.UnixNewlines(command));
		Task execution = cmd.ExecuteAsync(ct);

		// Если команда упала раньше (нет места, нет прав), дописывать вход незачем — прерываем запись
		using var writing = CancellationTokenSource.CreateLinkedTokenSource(ct);
		_ = execution.ContinueWith(_ =>
		{
			try
			{
				writing.Cancel();
			}
			catch (ObjectDisposedException)
			{
				// запись уже закончилась
			}
		}, TaskScheduler.Default);

		try
		{
			await using Stream input = cmd.CreateInputStream();
			await writeInput(input, writing.Token);
		}
		catch (OperationCanceledException) when (execution.IsCompleted && !ct.IsCancellationRequested)
		{
			// причину покажет код возврата и stderr команды
		}

		await execution;
		return new CommandResult(cmd.ExitStatus ?? -1, cmd.Result, cmd.Error);
	}

	public SftpClient GetSftp()
	{
		lock (m_sftpLock)
		{
			if (m_sftp is { IsConnected: true })
			{
				return m_sftp;
			}

			m_sftp?.Dispose();
			m_sftp = new SftpClient(m_info) { KeepAliveInterval = TimeSpan.FromSeconds(15) };
			m_sftp.HostKeyReceived += OnHostKeyReceived;
			m_sftp.Connect();
			return m_sftp;
		}
	}

	// Путь вида ~/x -> /home/deck/x для SFTP, который тильду не понимает
	public string ResolvePath(string path)
	{
		if (path == "~")
		{
			return Home;
		}

		return path.StartsWith("~/", StringComparison.Ordinal) ? Sh.JoinPath(Home, path[2..]) : path;
	}

	private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
	{
		HostKeyFingerprint = e.FingerPrintSHA256;
		bool trusted = Device.HostKeyFingerprint == null || Device.HostKeyFingerprint == e.FingerPrintSHA256;
		m_hostKeyMismatch = !trusted;
		e.CanTrust = trusted;
	}

	public void Dispose()
	{
		try
		{
			m_sftp?.Dispose();
			m_ssh.Dispose();
		}
		catch
		{
			// ignore
		}

		foreach (PrivateKeyFile key in m_keys)
		{
			key.Dispose();
		}
	}
}

using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SteamdeckViewer.Core;

// Команда из Unity (меню Build/Steam Deck): залить папку сборки на Deck и, если просили, запустить.
// Приходит параметрами запуска exe; если программа уже открыта, второй экземпляр передаёт команду
// работающему по именованному каналу и закрывается
public sealed record ExternalCommand(string Folder, bool Run)
{
	public static readonly string PipeName = "CarXDeckTools." + Environment.UserName;

	// CarXDeckTools.exe --deploy "D:\Build" [--run]
	public static ExternalCommand? Parse(IReadOnlyList<string> args)
	{
		string? folder = null;
		bool run = false;
		for (int i = 0; i < args.Count; i++)
		{
			if (args[i] == "--deploy" && i + 1 < args.Count)
			{
				folder = args[++i];
			}
			else if (args[i] == "--run")
			{
				run = true;
			}
		}

		return string.IsNullOrWhiteSpace(folder) ? null : new ExternalCommand(Normalize(folder), run);
	}

	// Профиль, папка сборки которого та же, что в команде: без учёта регистра, вида разделителей и завершающего «\»
	public BuildProfile? FindProfile(IEnumerable<BuildProfile> profiles)
	{
		return profiles.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.LocalFolder) &&
		                                    string.Equals(Normalize(p.LocalFolder), Normalize(Folder), StringComparison.OrdinalIgnoreCase));
	}

	public async Task<bool> SendAsync(string pipeName, TimeSpan timeout, CancellationToken ct)
	{
		try
		{
			await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			await client.ConnectAsync((int)timeout.TotalMilliseconds, ct);
			await client.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this) + "\n"), ct);
			return true;
		}
		catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	// Принимает команды до отмены: одно подключение — одна строка JSON
	public static async Task ListenAsync(string pipeName, Action<ExternalCommand> onCommand, CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			try
			{
				await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
					PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
				await server.WaitForConnectionAsync(ct);

				using var reader = new StreamReader(server, Encoding.UTF8);
				if (TryDeserialize(await reader.ReadLineAsync(ct)) is { } command)
				{
					onCommand(command);
				}
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (IOException)
			{
				// клиент отключился на полуслове или канал ещё занят прошлым подключением
				await Task.Delay(200, CancellationToken.None);
			}
		}
	}

	internal static ExternalCommand? TryDeserialize(string? line)
	{
		try
		{
			ExternalCommand? command = line == null ? null : JsonSerializer.Deserialize<ExternalCommand>(line);
			return string.IsNullOrWhiteSpace(command?.Folder) ? null : command;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static string Normalize(string path)
	{
		return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
	}
}

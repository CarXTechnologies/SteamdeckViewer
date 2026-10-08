using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SteamdeckViewer.Core;

// JS в интерфейсе Steam на Deck через CEF-отладку (Chrome DevTools Protocol), во вкладке SharedJSContext, где живёт
// SteamClient.*. Отладку Steam слушает на самом Deck (8080), к ней ведёт SSH-туннель. Включается она файлом
// ~/.steam/steam/.cef-enable-remote-debugging и перезапуском Steam (DeckControl.EnableCefDebuggingAsync).
// API SteamClient не документирован и меняется между версиями Steam
public static class SteamCef
{
	public const int DebugPort = 8080;
	private const string Context = "SharedJSContext";

	public static async Task<string?> EvaluateAsync(DeckConnection deck, string expression, CancellationToken ct)
	{
		using DeckTunnel tunnel = deck.OpenTunnel(DebugPort);
		return await EvaluateAsync("127.0.0.1:" + tunnel.LocalPort, expression, ct);
	}

	// origin — «хост:порт» отладки CEF
	internal static async Task<string?> EvaluateAsync(string origin, string expression, CancellationToken ct)
	{
		string targets;
		try
		{
			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
			targets = await http.GetStringAsync($"http://{origin}/json", ct);
		}
		catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
		{
			throw new SteamCefUnavailableException(e);
		}

		string path = DebuggerPath(targets) ?? throw new InvalidOperationException($"В интерфейсе Steam нет вкладки {Context}: Steam ещё загружается?");

		using var socket = new ClientWebSocket();
		await socket.ConnectAsync(new Uri($"ws://{origin}{path}"), ct);
		await socket.SendAsync(Encoding.UTF8.GetBytes(EvaluateRequest(1, expression)), WebSocketMessageType.Text, true, ct);

		var buffer = new byte[64 * 1024];
		while (true)
		{
			using var message = new MemoryStream();
			WebSocketReceiveResult received;
			do
			{
				received = await socket.ReceiveAsync(buffer, ct);
				if (received.MessageType == WebSocketMessageType.Close)
				{
					throw new InvalidOperationException("Steam закрыл соединение отладки.");
				}

				message.Write(buffer, 0, received.Count);
			}
			while (!received.EndOfMessage);

			if (ParseResponse(Encoding.UTF8.GetString(message.ToArray()), 1) is { } response)
			{
				// Без ожидания ответного кадра закрытия: CloseAsync ждал бы его сколько угодно
				await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
				return response.Error != null ? throw new InvalidOperationException("Ошибка в интерфейсе Steam: " + response.Error) : response.Value;
			}
		}
	}

	// Параметры запуска игры из библиотеки Steam (то же, что «Свойства → Параметры запуска»). Возвращает прежние:
	// appDetailsStore знает их только у игр, чьи свойства Steam уже загружал, иначе null
	public static Task<string?> SetLaunchOptionsAsync(DeckConnection deck, uint appId, string options, CancellationToken ct)
	{
		return EvaluateAsync(deck, LaunchOptionsScript(appId, options), ct);
	}

	internal static string LaunchOptionsScript(uint appId, string options)
	{
		return $$"""
			(() => {
				const before = window.appDetailsStore?.GetAppDetails?.({{appId}})?.strLaunchOptions ?? null;
				SteamClient.Apps.SetAppLaunchOptions({{appId}}, {{JsonSerializer.Serialize(options)}});
				return before;
			})()
			""";
	}

	// Путь WebSocket вкладки SharedJSContext из списка /json. Хост и порт в webSocketDebuggerUrl — со стороны Deck,
	// подключаемся к ним через туннель, поэтому берём только путь
	internal static string? DebuggerPath(string targetsJson)
	{
		using JsonDocument document = JsonDocument.Parse(targetsJson);
		foreach (JsonElement target in document.RootElement.EnumerateArray())
		{
			if (target.TryGetProperty("title", out JsonElement title) && title.GetString() == Context &&
			    target.TryGetProperty("webSocketDebuggerUrl", out JsonElement url) && Uri.TryCreate(url.GetString(), UriKind.Absolute, out Uri? uri))
			{
				return uri.PathAndQuery;
			}
		}

		return null;
	}

	internal static string EvaluateRequest(int id, string expression)
	{
		return JsonSerializer.Serialize(new
		{
			id,
			method = "Runtime.evaluate",
			@params = new { expression, returnByValue = true, awaitPromise = true }
		});
	}

	// null — сообщение не ответ на наш запрос (события CDP приходят вперемешку)
	internal static (string? Value, string? Error)? ParseResponse(string message, int id)
	{
		using JsonDocument document = JsonDocument.Parse(message);
		JsonElement root = document.RootElement;
		if (!root.TryGetProperty("id", out JsonElement responseId) || responseId.GetInt32() != id)
		{
			return null;
		}

		if (root.TryGetProperty("error", out JsonElement error))
		{
			return (null, error.TryGetProperty("message", out JsonElement text) ? text.GetString() : error.ToString());
		}

		JsonElement result = root.GetProperty("result");
		if (result.TryGetProperty("exceptionDetails", out JsonElement exception))
		{
			string? description = exception.TryGetProperty("exception", out JsonElement thrown) && thrown.TryGetProperty("description", out JsonElement d)
				? d.GetString()
				: exception.TryGetProperty("text", out JsonElement t) ? t.GetString() : null;
			return (null, description ?? "исключение");
		}

		JsonElement value = result.GetProperty("result");
		return value.TryGetProperty("value", out JsonElement v) && v.ValueKind == JsonValueKind.String ? (v.GetString(), null) : (null, null);
	}
}

// Порт отладки Steam не отвечает: отладка выключена или Steam не перезапускали после включения
public sealed class SteamCefUnavailableException(Exception inner)
	: Exception("CEF-отладка Steam недоступна. Включите её на вкладке «Устройство» и перезапустите Steam.", inner);

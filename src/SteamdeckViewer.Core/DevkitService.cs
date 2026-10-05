using System.Text;
using System.Text.Json;

namespace SteamdeckViewer.Core;

public sealed record DevkitProperties(string? Login, string RawJson);

// HTTP-служба SteamOS Devkit на Deck (порт 32000). Появляется после включения Developer Mode,
// протокол повторяет официальный SteamOS Devkit Client
public static class DevkitService
{
	public const int DefaultPort = 32000;

	// «Подпись» из клиента Valve: служба отбрасывает запросы на регистрацию без неё
	private const string MagicPhrase = "900b919520e4cf601998a71eec318fec";

	private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

	public static async Task<DevkitProperties?> GetPropertiesAsync(string host, int port, CancellationToken ct)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(TimeSpan.FromSeconds(5));

		try
		{
			string json = await Http.GetStringAsync($"http://{host}:{port}/properties.json", timeout.Token);
			using JsonDocument doc = JsonDocument.Parse(json);
			string? login = doc.RootElement.TryGetProperty("login", out JsonElement value) && value.ValueKind == JsonValueKind.String
				? value.GetString()
				: null;

			return new DevkitProperties(login, json);
		}
		catch (Exception) when (!ct.IsCancellationRequested)
		{
			return null;
		}
	}

	// Ответ приходит после того, как пользователь подтвердит сопряжение на экране Deck
	public static async Task<string> RegisterAsync(string host, int port, string publicKey, CancellationToken ct)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(TimeSpan.FromSeconds(120));

		string body = publicKey.Trim() + " " + MagicPhrase + "\n";
		using var content = new StringContent(body, Encoding.ASCII, "text/plain");
		using HttpResponseMessage response = await Http.PostAsync($"http://{host}:{port}/register", content, timeout.Token);

		string text = (await response.Content.ReadAsStringAsync(timeout.Token)).Trim();
		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {text}");
		}

		return text;
	}
}

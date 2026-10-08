using System.Text.RegularExpressions;

namespace SteamdeckViewer.Core;

public sealed record UpdateNotice(Version Version, string Url, string Message);

// Объявление о новой версии — текстовый документ, который ведёт автор программы. Удобнее всего Google Doc
// с доступом «Все, у кого есть ссылка»: его можно править прямо в браузере. Формат:
//   version: 1.0.1
//   url: https://drive.google.com/file/d/…/view
//   message: что нового — эта строка и все следующие
// Программа читает его при запуске и раз в несколько часов и, если версия новее своей, предлагает открыть ссылку
public static class AppUpdate
{
	private static readonly Regex GoogleDoc = new(@"^https://docs\.google\.com/document/d/([\w-]+)", RegexOptions.IgnoreCase);
	private static readonly Regex DriveFile = new(@"^https://drive\.google\.com/file/d/([\w-]+)", RegexOptions.IgnoreCase);

	// Ссылка «Поделиться» на Google Doc или файл на Google Drive открывает страницу, а не текст: переводим в выгрузку
	public static string ToTextUrl(string url)
	{
		url = url.Trim();
		if (GoogleDoc.Match(url) is { Success: true } doc)
		{
			return $"https://docs.google.com/document/d/{doc.Groups[1].Value}/export?format=txt";
		}

		if (DriveFile.Match(url) is { Success: true } file)
		{
			return $"https://drive.google.com/uc?export=download&id={file.Groups[1].Value}";
		}

		return url;
	}

	// null — в тексте нет версии или ссылки (документ не открыт для всех: Google вернул страницу входа)
	public static UpdateNotice? Parse(string text)
	{
		Version? version = null;
		string? url = null;
		var message = new List<string>();
		bool inMessage = false;

		foreach (string raw in text.TrimStart('﻿').Split('\n'))
		{
			string line = raw.TrimEnd('\r');
			if (inMessage)
			{
				message.Add(line);
				continue;
			}

			int colon = line.IndexOfAny([':', '=']);
			if (colon <= 0)
			{
				continue;
			}

			string key = line[..colon].Trim().ToLowerInvariant();
			string value = line[(colon + 1)..].Trim();
			switch (key)
			{
				case "version" or "версия":
					version = ParseVersion(value);
					break;
				case "url" or "link" or "ссылка":
					url = value;
					break;
				case "message" or "текст" or "что нового":
					message.Add(value);
					inMessage = true;
					break;
			}
		}

		return version != null && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https"
			? new UpdateNotice(version, url!, string.Join('\n', message).Trim())
			: null;
	}

	public static bool IsNewer(UpdateNotice notice, string currentVersion)
	{
		return ParseVersion(currentVersion) is not { } current || notice.Version > current;
	}

	public static async Task<UpdateNotice?> FetchAsync(HttpClient http, string documentUrl, CancellationToken ct)
	{
		return Parse(await http.GetStringAsync(ToTextUrl(documentUrl), ct));
	}

	// «1.0.1», «v1.2», «2» — недостающие части нулями, чтобы 1.0 и 1.0.0 были равны
	internal static Version? ParseVersion(string text)
	{
		string[] parts = text.Trim().TrimStart('v', 'V').Split('.');
		if (parts.Length is 0 or > 4 || parts.Any(p => !int.TryParse(p, out int n) || n < 0))
		{
			return null;
		}

		int[] numbers = parts.Select(int.Parse).Concat(Enumerable.Repeat(0, 4 - parts.Length)).ToArray();
		return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
	}
}

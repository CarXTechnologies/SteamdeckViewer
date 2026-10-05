namespace SteamdeckViewer.Core;

// Сохранённое устройство: адрес, учётная запись и запомненный отпечаток ключа хоста
public sealed class DeckDevice
{
	public string Name { get; set; } = "Steam Deck";
	public string Host { get; set; } = "steamdeck.local";
	public int SshPort { get; set; } = 22;
	public int DevkitPort { get; set; } = DevkitService.DefaultPort;
	public string User { get; set; } = "deck";

	// SHA256-отпечаток ключа хоста с первого подключения: смена ключа — повод насторожиться
	public string? HostKeyFingerprint { get; set; }

	// Логин и пароль веб-интерфейса Sunshine, которые приложение задало при установке на этот Deck
	public SunshineCredentials? Sunshine { get; set; }

	public override string ToString()
	{
		return $"{Name} — {User}@{Host}";
	}
}

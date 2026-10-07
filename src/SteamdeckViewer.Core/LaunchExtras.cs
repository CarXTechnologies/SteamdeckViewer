namespace SteamdeckViewer.Core;

// Добавки к профилю на один запуск: аргументы (запись профайлера) и переменные окружения (MangoHud)
public sealed record LaunchExtras(string? Arguments, IReadOnlyDictionary<string, string>? Environment);

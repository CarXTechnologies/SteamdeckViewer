namespace SteamdeckViewer.Core;

// Добавки к профилю на один запуск: аргументы (набор запуска, запись профайлера) и переменные окружения (набор, MangoHud)
public sealed record LaunchExtras(string? Arguments, IReadOnlyDictionary<string, string>? Environment)
{
	// Набор и добавки программы вместе; одноимённые переменные программы (MangoHud) важнее переменных набора
	public static LaunchExtras Combine(LaunchPreset? preset, string? arguments, IReadOnlyDictionary<string, string>? environment)
	{
		string joined = string.Join(' ', new[] { preset?.Arguments, arguments }.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!.Trim()));

		var merged = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (IReadOnlyDictionary<string, string>? source in new[] { preset == null ? null : BuildProfile.ParseEnvironment(preset.EnvironmentVariables), environment })
		{
			foreach ((string key, string value) in source ?? new Dictionary<string, string>())
			{
				merged[key] = value;
			}
		}

		return new LaunchExtras(joined.Length > 0 ? joined : null, merged.Count > 0 ? merged : null);
	}
}

// Набор аргументов и переменных окружения поверх профиля (-force-vulkan, флаги игры и т. п.):
// выбранный в профиле набор применяется ко всем запускам — кнопками программы и из Unity
public sealed class LaunchPreset
{
	public string Name { get; set; } = string.Empty;
	public string Arguments { get; set; } = string.Empty;
	public string EnvironmentVariables { get; set; } = string.Empty;

	public override string ToString()
	{
		return Name;
	}
}

namespace SteamdeckViewer.Core;

// Состояние устройства одним SSH-запросом: скрипт печатает строки key=value, разбираем их здесь
public sealed class DeckStatus
{
	internal const string Script = """
		. /etc/os-release 2>/dev/null
		echo "host=$(cat /etc/hostname 2>/dev/null)"
		echo "os=${PRETTY_NAME:-$NAME}"
		echo "version=$VERSION_ID"
		echo "build=$BUILD_ID"
		echo "kernel=$(uname -r)"
		echo "uptime=$(cut -d. -f1 /proc/uptime)"
		echo "load=$(cut -d' ' -f1-3 /proc/loadavg)"
		for b in /sys/class/power_supply/BAT*; do
			if [ -r "$b/capacity" ]; then echo "battery=$(cat "$b/capacity")"; echo "battery_status=$(cat "$b/status" 2>/dev/null)"; break; fi
		done
		for h in /sys/class/hwmon/hwmon*; do
			n=$(cat "$h/name" 2>/dev/null)
			case "$n" in k10temp|amdgpu|acpitz) [ -r "$h/temp1_input" ] && echo "temp_$n=$(cat "$h/temp1_input")";; esac
			case "$n" in jupiter|steamdeck_hwmon) [ -r "$h/fan1_input" ] && echo "fan=$(cat "$h/fan1_input")";; esac
		done
		echo "gpu_busy=$(cat /sys/class/drm/card*/device/gpu_busy_percent 2>/dev/null | head -n1)"
		echo "mem=$(awk '/^MemTotal:/{t=$2} /^MemAvailable:/{a=$2} END{print t, a}' /proc/meminfo)"
		echo "disk=$(df -P -B1 "$HOME" 2>/dev/null | awk 'NR==2{print $4, $2}')"
		if pgrep -x 'gamescope(-wl)?' >/dev/null 2>&1; then echo session=gamescope
		elif pgrep -x 'plasmashell|kwin_wayland|kwin_x11' >/dev/null 2>&1; then echo session=plasma
		else echo session=unknown; fi
		p=$(cat ~/.steam/steam.pid 2>/dev/null)
		if [ -n "$p" ] && kill -0 "$p" 2>/dev/null; then echo steam=1; else echo steam=0; fi
		if [ -e ~/.steam/steam/.cef-enable-remote-debugging ]; then echo cef=1; else echo cef=0; fi
		echo "ip=$(ip -4 -o addr show scope global 2>/dev/null | awk '{printf "%s %s  ", $2, $4}')"
		""";

	public IReadOnlyDictionary<string, string> Values { get; }

	private DeckStatus(IReadOnlyDictionary<string, string> values)
	{
		Values = values;
	}

	public string this[string key] => Values.TryGetValue(key, out string? value) ? value : string.Empty;

	public bool IsGameMode => this["session"] == "gamescope";
	public bool IsDesktop => this["session"] == "plasma";
	public bool SteamRunning => this["steam"] == "1";
	public bool CefDebuggingEnabled => this["cef"] == "1";

	public static async Task<DeckStatus> QueryAsync(DeckConnection deck, CancellationToken ct)
	{
		CommandResult result = await deck.RunAsync(Script, ct);
		return Parse(result.Output);
	}

	public static DeckStatus Parse(string output)
	{
		var values = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (string line in output.Split('\n'))
		{
			int eq = line.IndexOf('=');
			if (eq > 0)
			{
				values[line[..eq]] = line[(eq + 1)..].Trim();
			}
		}

		return new DeckStatus(values);
	}

	// Строки вкладки «Устройство» и отчёта для задачи: ключ, подпись
	public static readonly (string Key, string Label)[] Rows =
	[
		("host", "Устройство"),
		("os", "Система"),
		("kernel", "Ядро"),
		("session", "Режим"),
		("steam", "Steam"),
		("battery", "Батарея"),
		("temp", "Температура"),
		("gpu", "Загрузка GPU"),
		("fan", "Вентилятор"),
		("mem", "Память"),
		("disk", "Свободно в /home"),
		("ip", "Сеть"),
		("uptime", "Работает"),
		("cef", "CEF-отладка Steam")
	];

	// Значение строки Rows для человека; address — адрес подключения, если сеть Deck не определилась
	public string Display(string key, string address)
	{
		string value = key switch
		{
			"os" => $"{this["os"]} {string.Join(" ", new[] { this["version"], this["build"].Length > 0 ? $"(build {this["build"]})" : string.Empty }.Where(v => v.Length > 0))}".Trim(),
			"session" => IsGameMode ? "Game Mode (gamescope)" : IsDesktop ? "Рабочий стол (KDE Plasma)" : "не определён",
			"steam" => SteamRunning ? "запущен" : "не запущен",
			"battery" => this["battery"].Length > 0 ? $"{this["battery"]} % ({TranslateBattery(this["battery_status"])})" : string.Empty,
			"temp" => $"APU {Temperature("k10temp")}, GPU {Temperature("amdgpu")}",
			"gpu" => this["gpu_busy"].Length > 0 ? this["gpu_busy"] + " %" : string.Empty,
			"fan" => this["fan"].Length > 0 ? this["fan"] + " об/мин" : string.Empty,
			"mem" => FormatPair(this["mem"], kilobytes: true, "занято", used: true),
			"disk" => FormatPair(this["disk"], kilobytes: false, "из", used: false),
			"ip" => this["ip"].Length > 0 ? this["ip"] : address,
			"uptime" => long.TryParse(this["uptime"], out long seconds) ? FormatUptime(TimeSpan.FromSeconds(seconds)) : string.Empty,
			"cef" => CefDebuggingEnabled ? "включена (порт 8081 после перезапуска Steam)" : "выключена",
			_ => this[key]
		};

		return string.IsNullOrWhiteSpace(value) ? "—" : value;
	}

	// Температура из hwmon приходит в миллиградусах
	public string Temperature(string sensor)
	{
		return long.TryParse(this["temp_" + sensor], out long milli) ? $"{milli / 1000.0:0.#} °C" : "—";
	}

	public static string FormatBytes(long bytes)
	{
		string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
		double value = bytes;
		int unit = 0;
		while (value >= 1024 && unit < units.Length - 1)
		{
			value /= 1024;
			unit++;
		}

		return $"{value:0.#} {units[unit]}";
	}

	// "total available" из /proc/meminfo (КБ) или "available total" из df (байты)
	private static string FormatPair(string raw, bool kilobytes, string joiner, bool used)
	{
		string[] parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length != 2 || !long.TryParse(parts[0], out long a) || !long.TryParse(parts[1], out long b))
		{
			return "—";
		}

		long scale = kilobytes ? 1024 : 1;
		return used
			? $"{FormatBytes((a - b) * scale)} {joiner} из {FormatBytes(a * scale)}"
			: $"{FormatBytes(a * scale)} {joiner} {FormatBytes(b * scale)}";
	}

	private static string FormatUptime(TimeSpan time)
	{
		return time.TotalDays >= 1 ? $"{(int)time.TotalDays} д {time.Hours} ч" : $"{time.Hours} ч {time.Minutes} мин";
	}

	private static string TranslateBattery(string status)
	{
		return status switch
		{
			"Charging" => "заряжается",
			"Discharging" => "разряжается",
			"Full" => "заряжена",
			"Not charging" => "не заряжается",
			_ => status
		};
	}
}

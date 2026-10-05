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
}

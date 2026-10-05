using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamdeckViewer.Core;

public enum DeckScreenMode
{
	Unknown,
	GameMode,
	Desktop
}

// Running — служба Game Mode (root, KMS), DesktopRunning — служба рабочего стола (пользователь Deck, захват через KDE)
public sealed record SunshineStatus(
	bool AppInstalled,
	string? Version,
	bool ServiceInstalled,
	bool Running,
	bool WebUiUp,
	bool DesktopRunning = false,
	DeckScreenMode Session = DeckScreenMode.Unknown,
	bool CurrentLayout = true)
{
	public bool AnyRunning => Running || DesktopRunning;

	// Порт веб-интерфейса занят, а наших служб нет — Sunshine запущен иначе (например, через decky-sunshine)
	public bool ForeignInstance => WebUiUp && !AnyRunning;

	// Установка до разделения на две службы: настройки лежат у root, и служба рабочего стола их не видит
	public bool NeedsUpgrade => ServiceInstalled && !CurrentLayout;

	// Запущена служба не того режима: после переключения Deck между Game Mode и рабочим столом
	public bool WrongInstance => Session switch
	{
		DeckScreenMode.GameMode => DesktopRunning,
		DeckScreenMode.Desktop => Running,
		_ => false
	};
}

// Пользователь Deck, от имени которого работает служба рабочего стола; в его домашнем каталоге — её настройки
internal sealed record DeckAccount(string User, string Uid, string Gid, string Home);

// Запрос Moonlight на сопряжение, ждущий PIN: name — что прислал клиент (Moonlight шлёт одно и то же имя), address — откуда пришёл
public sealed record SunshinePairing(string Id, string Name, string Address);

internal readonly record struct ApiResponse(int Code, string Body);

public sealed record SunshineCredentials(string User, string Password)
{
	public static SunshineCredentials Generate()
	{
		const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
		return new SunshineCredentials("sdv", RandomNumberGenerator.GetString(alphabet, 20));
	}
}

// Sunshine на Deck в двух вариантах с общими настройками, ключами и сопряжениями:
// - Game Mode (gamescope) снимается только KMS-захватом, а он требует CAP_SYS_ADMIN. Поэтому, как в decky-sunshine,
//   flatpak Sunshine запускается службой от root, bwrap подменяется setuid-копией, чтобы песочница сохранила эту
//   возможность, а запуск и остановку пользователю deck разрешает правило polkit.
// - Рабочий стол KWin рисует с программным поворотом панели, и KMS-захват отдаёт его лёжа на боку. Здесь Sunshine
//   работает пользовательской службой deck и снимает экран через xdg-desktop-portal KDE, уже в нужной ориентации.
// Одновременно работает только один вариант: оба занимают одни и те же порты. Каталоги настроек у них свои,
// а одинаковыми их держит скрипт синхронизации, который служба Game Mode вызывает до запуска и после остановки
public static class SunshineHost
{
	public const string AppId = "dev.lizardbyte.app.Sunshine";
	public const string Unit = "sdv-sunshine.service";
	public const string DesktopUnit = "sdv-sunshine-desktop.service";
	public const int WebPort = 47990;

	internal const string StateDirectory = "/var/lib/steamdeckviewer";
	internal const string UnitPath = "/etc/systemd/system/" + Unit;
	internal const string PolkitRulePath = "/etc/polkit-1/rules.d/50-steamdeckviewer-sunshine.rules";
	internal const string UdevRulePath = "/etc/udev/rules.d/60-sunshine.rules";
	internal const string ModulesLoadPath = "/etc/modules-load.d/60-sunshine.conf";

	// Каталог настроек службы Game Mode. Песочница flatpak работает в своём пространстве пользователей, и root в ней
	// не может зайти в закрытый домашний каталог deck — поэтому у служб разные каталоги, а не один общий
	internal const string RootConfigDirectory = "/root/.var/app/" + AppId + "/config/sunshine";

	// Каталог настроек службы рабочего стола относительно домашнего каталога deck: Sunshine добавляет к нему «/sunshine»
	internal const string ConfigRootInHome = ".var/app/" + AppId + "/config";

	// Переносит настройки, ключи и сопряжения между каталогами двух служб. Запускается systemd от root вне песочницы
	internal const string SyncScriptPath = StateDirectory + "/sync-config.sh";

	// Без uaccess на /dev/uinput и /dev/uhid Sunshine от имени deck не создаст мышь, клавиатуру и геймпад.
	// Обычно правило берётся из самого flatpak, это — запасной вариант
	internal const string FallbackUdevRule =
		"KERNEL==\"uinput\", SUBSYSTEM==\"misc\", OPTIONS+=\"static_node=uinput\", GROUP=\"input\", MODE=\"0660\", TAG+=\"uaccess\"\n" +
		"KERNEL==\"uhid\", GROUP=\"input\", MODE=\"0660\", TAG+=\"uaccess\"\n";

	private const int DefaultMaxTime = 15;

	// Ответ на PIN ждёт обмена ключами с Moonlight (ping_timeout Sunshine, по умолчанию 10 с)
	private const int PinMaxTime = 45;

	private const string GamescopeRunning = "pgrep -x 'gamescope(-wl)?' >/dev/null 2>&1";
	private const string PlasmaRunning = "pgrep -x 'kwin_wayland|kwin_x11|plasmashell' >/dev/null 2>&1";

	// systemctl --user и flatpak permission-set из SSH-сеанса: каталог и шина пользователя могут быть не заданы
	private const string UserSession =
		"export XDG_RUNTIME_DIR=\"${XDG_RUNTIME_DIR:-/run/user/$(id -u)}\"\n" +
		"export DBUS_SESSION_BUS_ADDRESS=\"${DBUS_SESSION_BUS_ADDRESS:-unix:path=$XDG_RUNTIME_DIR/bus}\"\n";

	internal static readonly string StatusScript = UserSession + $$"""
		v=$(flatpak list --system --app --columns=application,version 2>/dev/null | awk '$1 == "{{AppId}}" {print $2; exit}')
		if [ -n "$(flatpak list --system --app --columns=application 2>/dev/null | grep -x '{{AppId}}')" ]; then echo app=1; else echo app=0; fi
		echo "version=$v"
		if [ -f {{UnitPath}} ]; then echo service=1; else echo service=0; fi
		if grep -q {{SyncScriptPath}} {{UnitPath}} 2>/dev/null; then echo layout=2; else echo layout=1; fi
		if systemctl is-active --quiet {{Unit}}; then echo running=1; else echo running=0; fi
		if systemctl --user is-active --quiet {{DesktopUnit}} 2>/dev/null; then echo desktop=1; else echo desktop=0; fi
		if {{GamescopeRunning}}; then echo session=game; elif {{PlasmaRunning}}; then echo session=desktop; else echo session=none; fi
		if (exec 3<>/dev/tcp/127.0.0.1/{{WebPort}}) 2>/dev/null; then echo web=1; else echo web=0; fi
		""";

	public static async Task<SunshineStatus> QueryAsync(DeckConnection deck, CancellationToken ct)
	{
		return ParseStatus((await deck.RunAsync(StatusScript, ct)).Output);
	}

	internal static SunshineStatus ParseStatus(string output)
	{
		DeckStatus values = DeckStatus.Parse(output);
		return new SunshineStatus(values["app"] == "1", values["version"].Length > 0 ? values["version"] : null,
			values["service"] == "1", values["running"] == "1", values["web"] == "1",
			DesktopRunning: values["desktop"] == "1",
			Session: ParseMode(values["session"]),
			CurrentLayout: values["layout"] != "1");
	}

	internal static DeckScreenMode ParseMode(string value)
	{
		return value switch
		{
			"game" => DeckScreenMode.GameMode,
			"desktop" => DeckScreenMode.Desktop,
			_ => DeckScreenMode.Unknown
		};
	}

	public static async Task<string> InstallAsync(DeckConnection deck, string sudoPassword, SunshineCredentials credentials, CancellationToken ct)
	{
		DeckAccount account = await AccountAsync(deck, ct);
		CommandResult host = await deck.RunAsync("cat /etc/hostname", ct);
		string script = InstallScript(account, credentials, host.Output.Trim());

		CommandResult result = await DeckSudo.RunScriptAsync(deck, sudoPassword, script, ct);
		if (!result.Success)
		{
			throw new InvalidOperationException("Установка Sunshine не удалась:\n" + Tail(result.Combined));
		}

		return result.Output;
	}

	public static async Task UninstallAsync(DeckConnection deck, string sudoPassword, CancellationToken ct)
	{
		DeckAccount account = await AccountAsync(deck, ct);

		// Пользовательскую службу и разрешение KDE убираем от имени deck: у root нет его сеанса
		await deck.RunAsync(UninstallUserScript(), ct);

		CommandResult result = await DeckSudo.RunScriptAsync(deck, sudoPassword, UninstallScript(account), ct);
		if (!result.Success)
		{
			throw new InvalidOperationException("Удаление Sunshine не удалось:\n" + Tail(result.Combined));
		}
	}

	// Запускает вариант для текущего режима Deck и останавливает другой; уже запущенный нужный вариант не трогает
	public static async Task<DeckScreenMode> StartAsync(DeckConnection deck, bool forceComposite, CancellationToken ct)
	{
		CommandResult result = await deck.RunAsync(StartScript(forceComposite), ct);
		DeckScreenMode mode = ParseMode(DeckStatus.Parse(result.Output)["mode"]);
		string journal = mode == DeckScreenMode.Desktop
			? $"journalctl --user -u {DesktopUnit} -n 50"
			: $"journalctl -u {Unit} -n 50";

		switch (result.ExitCode)
		{
			case 0:
				return mode;
			case 3 when result.Combined.Contains("authentication", StringComparison.OrdinalIgnoreCase):
				throw new InvalidOperationException("Deck не разрешает запускать службу Sunshine без пароля. Переустановите Sunshine.");
			case 3:
				throw new InvalidOperationException("Служба Sunshine не запустилась: " + result.Combined.Trim() + $"\n\nЖурнал на Deck: {journal}");
			case 4:
				throw new InvalidOperationException($"Sunshine запущен, но веб-интерфейс не отвечает за 30 секунд. Журнал на Deck: {journal}");
			case 6:
				throw new InvalidOperationException("Служба Sunshine для рабочего стола не запустилась: " + result.Combined.Trim() + $"\n\nЖурнал на Deck: {journal}");
			default:
				throw new InvalidOperationException($"Запуск Sunshine завершился с кодом {result.ExitCode}: {result.Combined.Trim()}");
		}
	}

	public static async Task StopAsync(DeckConnection deck, CancellationToken ct)
	{
		CommandResult result = await deck.RunAsync(StopScript(), ct);
		if (!result.Success)
		{
			throw new InvalidOperationException("Не удалось остановить Sunshine: " + result.Combined.Trim());
		}
	}

	private static async Task<DeckAccount> AccountAsync(DeckConnection deck, CancellationToken ct)
	{
		CommandResult result = await deck.RunAsync("id -u; id -g; printf '%s\\n' \"$HOME\"", ct);
		string[] lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (!result.Success || lines.Length < 3 || !lines[2].StartsWith('/') || lines[2].Any(c => char.IsWhiteSpace(c) || c is '\'' or '"' or '\\' or '$'))
		{
			throw new InvalidOperationException("Не удалось определить пользователя Deck: " + result.Combined.Trim());
		}

		return new DeckAccount(deck.Device.User, lines[0], lines[1], lines[2]);
	}

	// Запросы Moonlight, ждущие PIN (Sunshine 2026.9+); null — старая версия без такого списка,
	// в ней PIN подтверждается без pairing_id
	public static async Task<IReadOnlyList<SunshinePairing>?> PendingPairingsAsync(DeckConnection deck, SunshineCredentials credentials, CancellationToken ct)
	{
		ApiResponse response = await CallApiAsync(deck, credentials, "GET", "/api/pin", null, DefaultMaxTime, ct);
		return response.Code == 404 ? null : ParsePendingPairings(EnsureOk(response));
	}

	public static async Task CancelPairingAsync(DeckConnection deck, SunshineCredentials credentials, string pairingId, CancellationToken ct)
	{
		string body = JsonSerializer.Serialize(new Dictionary<string, string> { ["pairing_id"] = pairingId });
		await CallApiAsync(deck, credentials, "DELETE", "/api/pin", body, DefaultMaxTime, ct);
	}

	// Подтверждение PIN, который показывает Moonlight. Sunshine 2026.9+ отвечает только после того, как Moonlight
	// завершит обмен ключами (до ping_timeout, по умолчанию 10 с), и true значит, что сопряжение состоялось.
	// В старых версиях false — Sunshine пока не ждёт сопряжения
	public static async Task<bool> SendPinAsync(DeckConnection deck, SunshineCredentials credentials, string? pairingId, string pin, string clientName,
		CancellationToken ct)
	{
		string body = JsonSerializer.Serialize(PinBody(pairingId, pin, clientName));
		ApiResponse response = await CallApiAsync(deck, credentials, "POST", "/api/pin", body, PinMaxTime, ct);
		return IsStatusTrue(EnsureOk(response));
	}

	public static async Task<IReadOnlyList<string>> PairedClientsAsync(DeckConnection deck, SunshineCredentials credentials, CancellationToken ct)
	{
		ApiResponse response = await CallApiAsync(deck, credentials, "GET", "/api/clients/list", null, DefaultMaxTime, ct);
		return ParseClientNames(EnsureOk(response));
	}

	// Запрос нашего Moonlight: появился после запуска «moonlight pair» и пришёл с адреса этого ПК.
	// Если адрес не совпал (NAT, VPN), годится единственный новый запрос
	public static SunshinePairing? PickPairing(IReadOnlyList<SunshinePairing> pending, IReadOnlySet<string> knownIds, IReadOnlySet<string> localAddresses)
	{
		List<SunshinePairing> fresh = pending.Where(p => !knownIds.Contains(p.Id)).ToList();
		return fresh.LastOrDefault(p => localAddresses.Contains(p.Address)) ?? (fresh.Count == 1 ? fresh[0] : null);
	}

	internal static Dictionary<string, string> PinBody(string? pairingId, string pin, string clientName)
	{
		var body = new Dictionary<string, string>();
		if (pairingId != null)
		{
			body["pairing_id"] = pairingId;
		}

		body["pin"] = pin;
		body["name"] = clientName;
		return body;
	}

	// ---------------------------------------------------------------- скрипты

	// Файл с выбранным способом захвата для службы рабочего стола, относительно домашнего каталога deck
	internal const string DesktopEnvInHome = ".config/steamdeckviewer/sunshine-desktop.env";

	internal static string UserConfigDirectory(DeckAccount account)
	{
		return account.Home.TrimEnd('/') + "/" + ConfigRootInHome + "/sunshine";
	}

	// Перед запуском службы Game Mode (to-root) и после её остановки (to-user) каталог настроек копируется целиком,
	// если на исходной стороне sunshine_state.json новее: там ключи, uniqueid, логин веб-интерфейса и сопряжения.
	// Одновременно работает только одна служба, так что новее всегда каталог той, что работала последней
	internal static string SyncScript(DeckAccount account)
	{
		string home = account.Home.TrimEnd('/');
		string owner = $"{account.Uid}:{account.Gid}";
		return
			"#!/bin/bash\n" +
			"# SteamdeckViewer: Sunshine Game Mode (root) и рабочего стола (deck) с одними настройками и сопряжениями\n" +
			"set -eu\n" +
			$"root_cfg={RootConfigDirectory}\n" +
			$"user_cfg={UserConfigDirectory(account)}\n" +
			"state=sunshine_state.json\n" +
			"copy() {\n" +
			"  [ -f \"$1/$state\" ] || return 0\n" +
			"  if [ \"$3\" != force ] && [ -f \"$2/$state\" ] && [ ! \"$1/$state\" -nt \"$2/$state\" ]; then return 0; fi\n" +
			"  mkdir -p \"$(dirname \"$2\")\"\n" +
			"  rm -rf \"$2.sdv-new\"\n" +
			"  cp -a \"$1\" \"$2.sdv-new\"\n" +
			"  rm -rf \"$2\"\n" +
			"  mv \"$2.sdv-new\" \"$2\"\n" +
			"}\n" +
			"case \"${1:-}\" in\n" +
			"  to-root)\n" +
			"    copy \"$user_cfg\" \"$root_cfg\" \"${2:-}\"\n" +
			"    if [ -d \"$root_cfg\" ]; then chown -R root:root \"$root_cfg\"; fi\n" +
			"    ;;\n" +
			"  to-user)\n" +
			"    copy \"$root_cfg\" \"$user_cfg\" \"${2:-}\"\n" +
			$"    chown {owner} {home}/.var {home}/.var/app {home}/.var/app/{AppId} {home}/.var/app/{AppId}/config 2>/dev/null || true\n" +
			$"    if [ -d \"$user_cfg\" ]; then chown -R {owner} \"$user_cfg\"; fi\n" +
			"    ;;\n" +
			"  *)\n" +
			"    echo \"usage: $0 to-root|to-user [force]\" >&2\n" +
			"    exit 2\n" +
			"    ;;\n" +
			"esac\n";
	}

	internal static string InstallScript(DeckAccount account, SunshineCredentials credentials, string sunshineName)
	{
		string name = string.IsNullOrWhiteSpace(sunshineName) ? "steamdeck" : sunshineName;
		return
			"set -euo pipefail\n" +
			"echo '>>> flatpak Sunshine'\n" +
			"flatpak remote-add --system --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo\n" +
			$"flatpak install --system --noninteractive --or-update flathub {AppId}\n" +
			"echo '>>> служба Game Mode и правило polkit'\n" +
			$"mkdir -p {StateDirectory}\n" +
			$"cat > {UnitPath} <<'SDV_UNIT'\n{UnitFile(account)}SDV_UNIT\n" +
			$"mkdir -p {Sh.ParentPath(PolkitRulePath)}\n" +
			$"cat > {PolkitRulePath} <<'SDV_RULE'\n{PolkitRule(account.User)}SDV_RULE\n" +
			$"chmod 644 {UnitPath} {PolkitRulePath}\n" +
			"echo '>>> доступ к виртуальным устройствам ввода'\n" +
			$"app=$(flatpak info --system --show-location {AppId})\n" +
			"rules=\"$app/files/share/sunshine/udev/rules.d/60-sunshine.rules\"\n" +
			$"mkdir -p {Sh.ParentPath(UdevRulePath)} {Sh.ParentPath(ModulesLoadPath)}\n" +
			$"if [ -f \"$rules\" ]; then cp \"$rules\" {UdevRulePath}; else cat > {UdevRulePath} <<'SDV_UDEV'\n{FallbackUdevRule}SDV_UDEV\nfi\n" +
			$"echo uhid > {ModulesLoadPath}\n" +
			$"chmod 644 {UdevRulePath} {ModulesLoadPath}\n" +
			"modprobe uhid || true\n" +
			"udevadm control --reload-rules || true\n" +
			"udevadm trigger --property-match=DEVNAME=/dev/uinput || true\n" +
			"udevadm trigger --property-match=DEVNAME=/dev/uhid || true\n" +
			"echo '>>> настройки Sunshine'\n" +
			$"cat > {SyncScriptPath} <<'SDV_SYNC'\n{SyncScript(account)}SDV_SYNC\n" +
			$"chmod 755 {SyncScriptPath}\n" +
			// Сопряжения, сделанные на рабочем столе, сначала забираем к root, чтобы установка их не затёрла
			$"bash {SyncScriptPath} to-root\n" +
			$"cfg={RootConfigDirectory}\n" +
			"mkdir -p \"$cfg\"\n" +
			"conf=\"$cfg/sunshine.conf\"\n" +
			"touch \"$conf\"\n" +
			"set_key() { if grep -q \"^$1 *=\" \"$conf\"; then sed -i \"s|^$1 *=.*|$1 = $2|\" \"$conf\"; else printf '%s = %s\\n' \"$1\" \"$2\" >> \"$conf\"; fi; }\n" +
			"sed -i '/^capture *=/d' \"$conf\"\n" +
			"set_key encoder vaapi\n" +
			"set_key adapter_name /dev/dri/renderD128\n" +
			"set_key system_tray disabled\n" +
			"set_key origin_web_ui_allowed lan\n" +
			$"set_key sunshine_name {Sh.Quote(SafeName(name))}\n" +
			$"flatpak run --system --command=sunshine {AppId} --creds {Sh.Quote(credentials.User)} {Sh.Quote(credentials.Password)} >/dev/null\n" +
			"echo '>>> те же настройки для рабочего стола'\n" +
			$"bash {SyncScriptPath} to-user force\n" +
			"systemctl daemon-reload\n" +
			"echo '>>> готово'\n";
	}

	// Служба Game Mode. bwrap копируется заново при каждом запуске: после обновления SteamOS копия не отстанет
	// от системной. Настройки синхронизируются с каталогом службы рабочего стола до запуска и после остановки
	internal static string UnitFile(DeckAccount account)
	{
		return
			"[Unit]\n" +
			"Description=Sunshine (SteamdeckViewer, Game Mode)\n" +
			"After=network-online.target\n" +
			"Wants=network-online.target\n\n" +
			"[Service]\n" +
			"Type=simple\n" +
			$"Environment=FLATPAK_BWRAP={StateDirectory}/bwrap\n" +
			$"Environment=PULSE_SERVER=unix:/run/user/{account.Uid}/pulse/native\n" +
			$"ExecStartPre=/usr/bin/install -o root -g root -m 4755 /usr/bin/bwrap {StateDirectory}/bwrap\n" +
			$"ExecStartPre=-/usr/bin/bash {SyncScriptPath} to-root\n" +
			$"ExecStart=/usr/bin/flatpak run --system --socket=wayland --env=QT_QPA_PLATFORM=offscreen {AppId} capture=kms\n" +
			$"ExecStop=-/usr/bin/flatpak kill {AppId}\n" +
			$"ExecStopPost=-/usr/bin/bash {SyncScriptPath} to-user\n" +
			"Restart=on-failure\n" +
			"RestartSec=3\n";
	}

	// Служба рабочего стола — как собственная пользовательская служба flatpak Sunshine. Способ захвата
	// (portal на Wayland, x11 в сеансе X11) скрипт запуска пишет в файл окружения
	internal static string DesktopUnitFile()
	{
		return
			"[Unit]\n" +
			"Description=Sunshine (SteamdeckViewer, рабочий стол)\n" +
			"After=graphical-session.target xdg-desktop-autostart.target xdg-desktop-portal.service\n" +
			"PartOf=graphical-session.target\n" +
			"StartLimitIntervalSec=0\n\n" +
			"[Service]\n" +
			"Type=simple\n" +
			"Environment=SDV_CAPTURE=portal\n" +
			$"EnvironmentFile=-%h/{DesktopEnvInHome}\n" +
			$"ExecStart=/usr/bin/flatpak run --system --env=CONFIGURATION_DIRECTORY=%h/{ConfigRootInHome} {AppId} capture=${{SDV_CAPTURE}}\n" +
			$"ExecStop=-/usr/bin/flatpak kill {AppId}\n" +
			"Restart=on-failure\n" +
			"RestartSec=5\n";
	}

	internal static string PolkitRule(string user)
	{
		return
			"// SteamdeckViewer: пользователь Deck запускает и останавливает Sunshine без пароля\n" +
			"polkit.addRule(function(action, subject) {\n" +
			"    if (action.id == \"org.freedesktop.systemd1.manage-units\" &&\n" +
			$"        action.lookup(\"unit\") == \"{Unit}\" &&\n" +
			$"        subject.user == \"{SafeName(user)}\") {{\n" +
			"        var verb = action.lookup(\"verb\");\n" +
			"        if (verb == \"start\" || verb == \"stop\" || verb == \"restart\") {\n" +
			"            return polkit.Result.YES;\n" +
			"        }\n" +
			"    }\n" +
			"});\n";
	}

	internal static string UninstallScript(DeckAccount account)
	{
		string home = account.Home.TrimEnd('/');
		return
			$"systemctl stop {Unit} 2>/dev/null || true\n" +
			$"rm -f {UnitPath} {PolkitRulePath} {UdevRulePath} {ModulesLoadPath}\n" +
			$"rm -rf {StateDirectory}\n" +
			"systemctl daemon-reload\n" +
			"udevadm control --reload-rules || true\n" +
			$"flatpak uninstall --system --noninteractive --delete-data {AppId} || true\n" +
			$"rm -rf {home}/.var/app/{AppId}\n";
	}

	internal static string UninstallUserScript()
	{
		return
			UserSession +
			$"systemctl --user stop {DesktopUnit} 2>/dev/null || true\n" +
			$"rm -f \"$HOME/.config/systemd/user/{DesktopUnit}\" \"$HOME/{DesktopEnvInHome}\"\n" +
			"systemctl --user daemon-reload 2>/dev/null || true\n" +
			$"flatpak permission-remove kde-authorized remote-desktop {AppId} 2>/dev/null || true\n";
	}

	// Вариант выбирается по тому, что сейчас на экране Deck: gamescope — служба Game Mode, иначе — служба рабочего стола.
	// Перед запуском одной другая останавливается: им нужны одни и те же порты.
	// Разрешение kde-authorized убирает запрос на удалённое управление на экране Deck.
	// Обход чёрного экрана Sunshine 2026.906+ в Game Mode (LizardByte/Sunshine#5839): принудительная композиция gamescope
	internal static string StartScript(bool forceComposite)
	{
		return
			UserSession +
			$"if {GamescopeRunning}; then\n" +
			"  echo mode=game\n" +
			$"  systemctl --user stop {DesktopUnit} 2>/dev/null || true\n" +
			$"  systemctl start {Unit} 2>&1 || exit 3\n" +
			"else\n" +
			"  echo mode=desktop\n" +
			$"  systemctl stop {Unit} 2>&1 || exit 3\n" +
			"  capture=portal\n" +
			"  if pgrep -x kwin_x11 >/dev/null 2>&1; then capture=x11; fi\n" +
			$"  flatpak permission-set kde-authorized remote-desktop {AppId} yes >/dev/null 2>&1 || echo 'warning: flatpak permission-set kde-authorized failed'\n" +
			$"  mkdir -p \"$HOME/.config/systemd/user\" \"$(dirname \"$HOME/{DesktopEnvInHome}\")\"\n" +
			$"  printf 'SDV_CAPTURE=%s\\n' \"$capture\" > \"$HOME/{DesktopEnvInHome}\"\n" +
			$"  cat > \"$HOME/.config/systemd/user/{DesktopUnit}\" <<'SDV_UNIT'\n{DesktopUnitFile()}SDV_UNIT\n" +
			"  systemctl --user daemon-reload 2>&1 || exit 6\n" +
			$"  systemctl --user start {DesktopUnit} 2>&1 || exit 6\n" +
			"fi\n" +
			"i=0\n" +
			$"until (exec 3<>/dev/tcp/127.0.0.1/{WebPort}) 2>/dev/null; do i=$((i+1)); [ $i -ge 30 ] && exit 4; sleep 1; done\n" +
			(forceComposite ? $"if {GamescopeRunning}; then {CompositeCommand(1)}; fi\n" : string.Empty) +
			"exit 0";
	}

	internal static string StopScript()
	{
		return
			UserSession +
			$"if {GamescopeRunning}; then {CompositeCommand(0)}; fi\n" +
			$"systemctl --user stop {DesktopUnit} 2>/dev/null || true\n" +
			$"systemctl stop {Unit} 2>&1";
	}

	private static string CompositeCommand(int value)
	{
		return $"DISPLAY=:0 xprop -root -f GAMESCOPE_COMPOSITE_FORCE 32c -set GAMESCOPE_COMPOSITE_FORCE {value} 2>/dev/null || true";
	}

	// ---------------------------------------------------------------- API

	// API вызывается curl на самом Deck: не мешают ни VPN на ПК, ни самоподписанный сертификат,
	// а логин и пароль идут конфигом через stdin, а не аргументами (их видно в списке процессов)
	private static async Task<ApiResponse> CallApiAsync(DeckConnection deck, SunshineCredentials credentials, string method, string path, string? jsonBody,
		int maxTimeSeconds, CancellationToken ct)
	{
		byte[] config = Encoding.UTF8.GetBytes(CurlConfig(credentials, method, path, jsonBody, maxTimeSeconds));
		CommandResult result = await deck.RunWithInputAsync("curl -K -", async (input, token) => await input.WriteAsync(config, token), ct);
		return result.ExitCode switch
		{
			0 => ParseCurlOutput(result.Output),
			7 => throw new InvalidOperationException("Sunshine не запущен."),
			_ => throw new InvalidOperationException($"Запрос к Sunshine не удался (curl {result.ExitCode}): {result.Combined.Trim()}")
		};
	}

	private static string EnsureOk(ApiResponse response)
	{
		return response.Code switch
		{
			>= 200 and < 300 => response.Body,
			401 => throw new InvalidOperationException(
				"Sunshine не принял логин и пароль веб-интерфейса. Переустановите Sunshine кнопкой «Установить…», чтобы задать их заново."),
			_ => throw new InvalidOperationException($"Sunshine вернул ошибку {response.Code}: {response.Body.Trim()}")
		};
	}

	// Код ответа дописывается последней строкой, чтобы отличать 401 от 404 и прочих отказов
	internal static string CurlConfig(SunshineCredentials credentials, string method, string path, string? jsonBody, int maxTimeSeconds = DefaultMaxTime,
		string baseUrl = "https://127.0.0.1:47990")
	{
		var config = new StringBuilder();
		config.Append($"url = \"{Escape(baseUrl + path)}\"\n");
		config.Append($"request = \"{method}\"\n");
		config.Append($"user = \"{Escape(credentials.User)}:{Escape(credentials.Password)}\"\n");
		config.Append($"insecure\nsilent\nshow-error\nmax-time = {maxTimeSeconds}\n");
		config.Append("write-out = \"\\n%{http_code}\"\n");
		if (jsonBody != null)
		{
			config.Append("header = \"Content-Type: application/json\"\n");
			config.Append($"data = \"{Escape(jsonBody)}\"\n");
		}

		return config.ToString();
	}

	internal static ApiResponse ParseCurlOutput(string output)
	{
		string text = output.TrimEnd('\r', '\n');
		int split = text.LastIndexOf('\n');
		string code = split < 0 ? text : text[(split + 1)..];
		return int.TryParse(code.Trim(), out int value)
			? new ApiResponse(value, split < 0 ? "" : text[..split].TrimEnd('\r'))
			: new ApiResponse(0, output);
	}

	internal static IReadOnlyList<SunshinePairing> ParsePendingPairings(string json)
	{
		try
		{
			using JsonDocument doc = JsonDocument.Parse(json);
			if (!doc.RootElement.TryGetProperty("pairings", out JsonElement pairings) || pairings.ValueKind != JsonValueKind.Array)
			{
				return [];
			}

			return pairings.EnumerateArray()
				.Select(p => new SunshinePairing(Text(p, "id"), Text(p, "name"), Text(p, "address")))
				.Where(p => p.Id.Length > 0)
				.ToList();
		}
		catch (JsonException)
		{
			return [];
		}

		static string Text(JsonElement element, string name)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
		}
	}

	internal static bool IsStatusTrue(string json)
	{
		try
		{
			using JsonDocument doc = JsonDocument.Parse(json);
			return doc.RootElement.TryGetProperty("status", out JsonElement status) &&
			       (status.ValueKind == JsonValueKind.True ||
			        status.ValueKind == JsonValueKind.String && status.GetString() == "true");
		}
		catch (JsonException)
		{
			return false;
		}
	}

	internal static IReadOnlyList<string> ParseClientNames(string json)
	{
		try
		{
			using JsonDocument doc = JsonDocument.Parse(json);
			if (!doc.RootElement.TryGetProperty("named_certs", out JsonElement certs) || certs.ValueKind != JsonValueKind.Array)
			{
				return [];
			}

			return certs.EnumerateArray()
				.Select(c => c.TryGetProperty("name", out JsonElement n) ? n.GetString() : null)
				.OfType<string>()
				.ToList();
		}
		catch (JsonException)
		{
			return [];
		}
	}

	private static string Escape(string value)
	{
		return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
	}

	// Имя для конфига, правила polkit и Moonlight: без кавычек и пробелов
	internal static string SafeName(string value)
	{
		var name = new StringBuilder();
		foreach (char c in value)
		{
			name.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
		}

		return name.Length > 0 ? name.ToString() : "steamdeck";
	}

	private static string Tail(string text)
	{
		string[] lines = text.Trim().Split('\n');
		return string.Join('\n', lines.Skip(Math.Max(0, lines.Length - 25)));
	}
}

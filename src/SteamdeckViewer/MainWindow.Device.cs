using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SteamdeckViewer.Core;

namespace SteamdeckViewer;

// Вкладка «Устройство»: состояние Deck, режимы, питание, SSH-терминал и CEF-отладка Steam
internal sealed partial class MainWindow
{
	private static readonly (string Key, string Label)[] StatusRows =
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

	private readonly Dictionary<string, TextBlock> m_statusValues = [];
	private readonly CheckBox m_cbAutoRefresh = new() { Content = "Обновлять каждые 5 с" };
	private readonly DispatcherTimer m_statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
	private readonly TextBlock m_hostKeyText = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontFamily = Ui.Mono, FontSize = 12 };
	private DeckStatus? m_lastStatus;
	private bool m_statusRefreshing;

	private Control BuildDeviceTab()
	{
		var values = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
		for (int i = 0; i < StatusRows.Length; i++)
		{
			values.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

			var label = new TextBlock { Text = StatusRows[i].Label, Opacity = 0.75, Margin = new Thickness(0, 0, 16, 6) };
			Grid.SetRow(label, i);
			values.Children.Add(label);

			var value = new SelectableTextBlock { Text = "—", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
			Grid.SetRow(value, i);
			Grid.SetColumn(value, 1);
			values.Children.Add(value);
			m_statusValues[StatusRows[i].Key] = value;
		}

		m_cbAutoRefresh.IsChecked = m_settings.AutoRefreshStatus;
		m_cbAutoRefresh.IsCheckedChanged += (_, _) =>
		{
			m_settings.AutoRefreshStatus = m_cbAutoRefresh.IsChecked == true;
			m_settings.Save();
		};

		m_statusTimer.Tick += async (_, _) =>
		{
			if (m_deck != null && m_cbAutoRefresh.IsChecked == true && m_tabs.SelectedIndex == 0 && IsActive)
			{
				await RefreshStatusAsync();
			}
		};
		m_statusTimer.Start();

		Control statusGroup = Ui.Group("Состояние", Ui.Column(10,
			values,
			Ui.Row(Ui.Button("Обновить", RefreshStatusAsync), m_cbAutoRefresh)));

		var actions = Ui.Column(10,
			Ui.Group("Режим", Ui.Column(8,
				Wide(Ui.Button("Game Mode", () => SwitchSessionAsync(DeckSession.GameMode))),
				Wide(Ui.Button("Рабочий стол Linux", () => SwitchSessionAsync(DeckSession.Desktop))),
				Wide(Ui.Button("Перезапустить Steam", RestartSteamAsync)))),
			Ui.Group("Питание", Ui.Column(8,
				Wide(Ui.Button("Перезагрузить", RebootAsync)),
				Wide(Ui.Button("Выключить", PowerOffAsync)))),
			Ui.Group("Инструменты", Ui.Column(8,
				Wide(Ui.Button("SSH-терминал", OpenSshTerminalAsync)),
				Wide(Ui.Button("Включить CEF-отладку Steam", EnableCefAsync)),
				Wide(Ui.Button("Открыть CEF DevTools", OpenCefDevToolsAsync)),
				Ui.Hint("CEF DevTools — интерфейс Steam (Big Picture) в браузере: вкладки SharedJSContext, QuickAccess и др. Порт 8081 открывается после включения отладки и перезапуска Steam."))),
			Ui.Group("Ключ хоста (SHA256)", m_hostKeyText));
		actions.Width = 320;

		var root = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 10) };
		root.Children.Add(new ScrollViewer { Content = statusGroup });
		var actionsScroll = new ScrollViewer { Content = actions, Margin = new Thickness(10, 0, 0, 0) };
		Grid.SetColumn(actionsScroll, 1);
		root.Children.Add(actionsScroll);
		return root;
	}

	private static Button Wide(Button button)
	{
		button.HorizontalAlignment = HorizontalAlignment.Stretch;
		button.HorizontalContentAlignment = HorizontalAlignment.Center;
		return button;
	}

	private async Task RefreshStatusAsync()
	{
		DeckConnection? deck = m_deck;
		if (deck == null || m_statusRefreshing)
		{
			return;
		}

		m_statusRefreshing = true;
		try
		{
			DeckStatus status = await Task.Run(() => DeckStatus.QueryAsync(deck, m_lifetime.Token));
			ShowStatus(status, deck);
		}
		catch (Exception e)
		{
			SetStatus("Не удалось получить состояние: " + e.Message);
			if (!deck.IsConnected)
			{
				Disconnect();
			}
		}
		finally
		{
			m_statusRefreshing = false;
		}
	}

	private void ShowStatus(DeckStatus s, DeckConnection deck)
	{
		m_lastStatus = s;

		string version = string.Join(" ", new[] { s["version"], s["build"].Length > 0 ? $"(build {s["build"]})" : string.Empty }.Where(v => v.Length > 0));
		SetValue("host", s["host"]);
		SetValue("os", $"{s["os"]} {version}".Trim());
		SetValue("kernel", s["kernel"]);
		SetValue("session", s.IsGameMode ? "Game Mode (gamescope)" : s.IsDesktop ? "Рабочий стол (KDE Plasma)" : "не определён");
		SetValue("steam", s.SteamRunning ? "запущен" : "не запущен");
		SetValue("battery", s["battery"].Length > 0 ? $"{s["battery"]} % ({TranslateBattery(s["battery_status"])})" : "—");
		SetValue("temp", $"APU {s.Temperature("k10temp")}, GPU {s.Temperature("amdgpu")}");
		SetValue("gpu", s["gpu_busy"].Length > 0 ? s["gpu_busy"] + " %" : "—");
		SetValue("fan", s["fan"].Length > 0 ? s["fan"] + " об/мин" : "—");
		SetValue("mem", FormatPair(s["mem"], kilobytes: true, "занято", used: true));
		SetValue("disk", FormatPair(s["disk"], kilobytes: false, "из", used: false));
		SetValue("ip", s["ip"].Length > 0 ? s["ip"] : deck.Device.Host);
		SetValue("uptime", long.TryParse(s["uptime"], out long seconds) ? FormatUptime(TimeSpan.FromSeconds(seconds)) : "—");
		SetValue("cef", s.CefDebuggingEnabled ? "включена (порт 8081 после перезапуска Steam)" : "выключена");

		m_hostKeyText.Text = deck.HostKeyFingerprint;
	}

	private void ClearStatusView()
	{
		m_lastStatus = null;
		foreach (TextBlock value in m_statusValues.Values)
		{
			value.Text = "—";
		}

		m_hostKeyText.Text = string.Empty;
	}

	private void SetValue(string key, string value)
	{
		m_statusValues[key].Text = string.IsNullOrWhiteSpace(value) ? "—" : value;
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
			? $"{DeckStatus.FormatBytes((a - b) * scale)} {joiner} из {DeckStatus.FormatBytes(a * scale)}"
			: $"{DeckStatus.FormatBytes(a * scale)} {joiner} {DeckStatus.FormatBytes(b * scale)}";
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

	// ---------------------------------------------------------------- действия

	private async Task SwitchSessionAsync(DeckSession session)
	{
		string name = session == DeckSession.GameMode ? "Game Mode" : "рабочий стол";
		await RunCommandAsync($"Переключение в {name}…", (deck, ct) => DeckControl.SwitchSessionAsync(deck, session, ct));
		await Task.Delay(3000);
		await RefreshStatusAsync();
	}

	private async Task RestartSteamAsync()
	{
		if (await Dialogs.YesNo(this, "Перезапустить Steam (сессию Game Mode)? Запущенная игра будет закрыта.", "Перезапуск Steam"))
		{
			await RunCommandAsync("Перезапуск Steam…", DeckControl.RestartSteamAsync);
		}
	}

	private async Task RebootAsync()
	{
		if (await Dialogs.YesNo(this, "Перезагрузить Deck?", "Перезагрузка"))
		{
			if (await RunCommandAsync("Перезагрузка…", DeckControl.RebootAsync))
			{
				Disconnect();
			}
		}
	}

	private async Task PowerOffAsync()
	{
		if (await Dialogs.YesNo(this, "Выключить Deck? Включить его обратно удалённо не получится.", "Выключение"))
		{
			if (await RunCommandAsync("Выключение…", DeckControl.PowerOffAsync))
			{
				Disconnect();
			}
		}
	}

	private async Task EnableCefAsync()
	{
		if (await RunCommandAsync("Включение CEF-отладки…", DeckControl.EnableCefDebuggingAsync))
		{
			await Dialogs.Info(this, "Отладка CEF включена. Она заработает после перезапуска Steam («Перезапустить Steam»).");
			await RefreshStatusAsync();
		}
	}

	private async Task OpenCefDevToolsAsync()
	{
		if (m_deck == null)
		{
			await Dialogs.Info(this, "Нет подключения к Deck.");
			return;
		}

		await Launcher.LaunchUriAsync(new Uri($"http://{m_deck.Device.Host}:8081"));
	}

	// Полноценный терминал даёт ssh.exe из Windows; ключ тот же, что у приложения
	private async Task OpenSshTerminalAsync()
	{
		DeckDevice? device = m_deck?.Device ?? SelectedDevice;
		IReadOnlyList<string> keys = DeckKeys.AvailablePrivateKeys();
		if (device == null || keys.Count == 0)
		{
			await Dialogs.Info(this, "Сначала выполните сопряжение с Deck.");
			return;
		}

		string sshArgs = $"-i \"{keys[0]}\" -p {device.SshPort} -o StrictHostKeyChecking=accept-new {device.User}@{device.Host}";
		try
		{
			Process.Start(new ProcessStartInfo("wt.exe", $"new-tab --title \"{device.Name}\" ssh {sshArgs}") { UseShellExecute = true });
		}
		catch
		{
			try
			{
				Process.Start(new ProcessStartInfo("cmd.exe", $"/c start \"{device.Name}\" ssh {sshArgs}") { UseShellExecute = true });
			}
			catch (Exception e)
			{
				await Dialogs.Error(this, "Не удалось открыть терминал: " + e.Message);
			}
		}
	}

	private Task<bool> RunCommandAsync(string busyText, Func<DeckConnection, CancellationToken, Task<CommandResult>> command)
	{
		return RunOnDeckAsync(busyText, async (deck, ct) =>
		{
			CommandResult result = await command(deck, ct);
			if (!result.Success)
			{
				throw new InvalidOperationException($"Команда завершилась с кодом {result.ExitCode}: {result.Combined.Trim()}");
			}
		});
	}
}

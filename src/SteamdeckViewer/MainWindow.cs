using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Renci.SshNet.Common;
using SteamdeckViewer.Core;

namespace SteamdeckViewer;

// Главное окно: сверху выбор Deck и подключение, ниже вкладки (устройство, файлы, билды, отладка, консоль)
internal sealed partial class MainWindow : Window
{
	private readonly AppSettings m_settings = AppSettings.Current;
	private readonly CancellationTokenSource m_lifetime = new();

	private DeckConnection? m_deck;
	private int m_busyCount;

	// Устройство и подключение
	private readonly ComboBox m_cbDevices = new() { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
	private readonly Button m_btnConnect = new() { MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
	private readonly TextBlock m_connectionText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

	// Строка состояния
	private readonly TextBlock m_statusText = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
	private readonly ProgressBar m_statusBusy = new() { IsIndeterminate = true, Width = 120, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };

	private readonly TabControl m_tabs = new() { Margin = new Thickness(10, 0, 10, 0) };

	public MainWindow()
	{
		Title = "Steamdeck Viewer";
		MinWidth = 1100;
		MinHeight = 700;
		Width = 1440;
		Height = 900;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;

		Content = BuildLayout();

		ReloadDevices();
		m_cbDevices.SelectionChanged += (_, _) => OnDeviceSelected();
		m_btnConnect.Click += async (_, _) => await ToggleConnectionAsync();
		UpdateConnectionState();

		Opened += async (_, _) =>
		{
			if (m_settings.AutoConnect && SelectedDevice != null && DeckKeys.AvailablePrivateKeys().Count > 0)
			{
				await ConnectAsync(quiet: true);
			}
		};

		Closed += (_, _) =>
		{
			m_lifetime.Cancel();
			StopLogTail();
			m_relay.Dispose();
			m_deck?.Dispose();
			m_settings.Save();
		};
	}

	private DeckDevice? SelectedDevice => m_cbDevices.SelectedItem as DeckDevice;

	private Control BuildLayout()
	{
		// У Fluent заголовки вкладок 24 pt — для пяти вкладок слишком крупно
		m_tabs.Resources["TabItemHeaderFontSize"] = 17.0;
		m_tabs.Items.Add(new TabItem { Header = "Устройство", Content = BuildDeviceTab() });
		m_tabs.Items.Add(new TabItem { Header = "Файлы", Content = BuildFilesTab() });
		m_tabs.Items.Add(new TabItem { Header = "Билды", Content = BuildBuildsTab() });
		m_tabs.Items.Add(new TabItem { Header = "Отладка (Rider)", Content = BuildDebugTab() });
		m_tabs.Items.Add(new TabItem { Header = "Консоль", Content = BuildConsoleTab() });

		Control menu = BuildMenu();
		Control deviceBar = BuildDeviceBar();

		var statusBar = new Border
		{
			Padding = new Thickness(8, 4, 8, 4),
			BorderThickness = new Thickness(0, 1, 0, 0),
			BorderBrush = Ui.GroupBorderBrush,
			Child = new DockPanel
			{
				Children =
				{
					m_statusBusy,
					m_statusText
				}
			}
		};
		DockPanel.SetDock(m_statusBusy, Dock.Right);

		var root = new DockPanel();
		DockPanel.SetDock(menu, Dock.Top);
		root.Children.Add(menu);
		DockPanel.SetDock(deviceBar, Dock.Top);
		root.Children.Add(deviceBar);
		DockPanel.SetDock(statusBar, Dock.Bottom);
		root.Children.Add(statusBar);
		root.Children.Add(m_tabs);
		return root;
	}

	private Control BuildMenu()
	{
		var themeSystem = new MenuItem { Header = "Как в системе" };
		var themeLight = new MenuItem { Header = "Светлая" };
		var themeDark = new MenuItem { Header = "Тёмная" };
		themeSystem.Click += (_, _) => ChangeTheme(AppColorMode.System);
		themeLight.Click += (_, _) => ChangeTheme(AppColorMode.Light);
		themeDark.Click += (_, _) => ChangeTheme(AppColorMode.Dark);

		var autoConnect = new MenuItem { Header = "Подключаться при запуске", ToggleType = MenuItemToggleType.CheckBox, IsChecked = m_settings.AutoConnect };
		autoConnect.Click += (_, _) =>
		{
			m_settings.AutoConnect = !m_settings.AutoConnect;
			autoConnect.IsChecked = m_settings.AutoConnect;
			m_settings.Save();
		};

		var openKeys = new MenuItem { Header = "Открыть папку SSH-ключей" };
		openKeys.Click += (_, _) => OpenLocalFolder(DeckKeys.KeysDirectory);

		var help = new MenuItem { Header = "Как подготовить Deck" };
		help.Click += async (_, _) => await Dialogs.Info(this, SetupHelpText, "Подготовка Steam Deck");

		return new Menu
		{
			Items =
			{
				new MenuItem { Header = "Вид", Items = { new MenuItem { Header = "Тема", Items = { themeSystem, themeLight, themeDark } } } },
				new MenuItem { Header = "Настройки", Items = { autoConnect, openKeys } },
				new MenuItem { Header = "Справка", Items = { help } }
			}
		};
	}

	private Control BuildDeviceBar()
	{
		var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

		var left = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
		left.Children.Add(m_cbDevices);
		var deviceButtons = Ui.Row(
			Ui.Button("Найти в сети", DiscoverAsync),
			Ui.Button("Добавить…", AddDeviceAsync),
			Ui.Button("Изменить…", EditDeviceAsync),
			Ui.Button("Удалить", RemoveDeviceAsync));
		deviceButtons.Margin = new Thickness(8, 0, 0, 0);
		Grid.SetColumn(deviceButtons, 1);
		left.Children.Add(deviceButtons);
		row.Children.Add(left);

		var pairButtons = Ui.Row(
			Ui.Button("Сопрячь (Developer Mode)", PairAsync),
			Ui.Button("Ключ по паролю…", InstallKeyWithPasswordAsync),
			m_btnConnect);
		pairButtons.Margin = new Thickness(16, 0, 0, 0);
		Grid.SetColumn(pairButtons, 1);
		row.Children.Add(pairButtons);

		Grid.SetColumn(m_connectionText, 2);
		row.Children.Add(m_connectionText);

		Border group = Ui.Group("Steam Deck", row);
		group.Margin = new Thickness(10, 6, 10, 8);
		return group;
	}

	// ---------------------------------------------------------------- устройства

	private void ReloadDevices(DeckDevice? select = null)
	{
		m_cbDevices.ItemsSource = null;
		m_cbDevices.ItemsSource = m_settings.Devices.ToList();

		int index = select != null ? m_settings.Devices.IndexOf(select) : m_settings.SelectedDevice;
		m_cbDevices.SelectedIndex = m_settings.Devices.Count == 0 ? -1 : Math.Clamp(index, 0, m_settings.Devices.Count - 1);
	}

	private void OnDeviceSelected()
	{
		if (m_cbDevices.SelectedIndex >= 0)
		{
			m_settings.SelectedDevice = m_cbDevices.SelectedIndex;
			m_settings.Save();
		}

		if (m_deck != null && m_deck.Device != SelectedDevice)
		{
			Disconnect();
		}
	}

	private async Task DiscoverAsync()
	{
		SetBusy(true, "Поиск Deck в сети (mDNS)…");
		try
		{
			DiscoveryResult result = await Task.Run(() => DeckDiscovery.FindAsync(TimeSpan.FromSeconds(3), m_lifetime.Token));

			// Офисные и гостевые сети часто режут multicast — тогда спрашиваем, можно ли перебрать свои подсети
			IReadOnlyList<LocalNetwork> networks = DeckDiscovery.ScannableNetworks();
			if (result.Decks.Count == 0 && !result.BlockedByFirewall && networks.Count > 0 &&
			    await Dialogs.YesNo(this,
				    "По mDNS Deck не ответил: в офисных и гостевых сетях multicast часто фильтруется.\n\n" +
				    $"Проверить адреса подсетей {string.Join(", ", networks)} — подключиться к порту {DevkitService.DefaultPort} службы devkit? Займёт несколько секунд.",
				    "Поиск Deck"))
			{
				var progress = new Progress<double>(p => SetStatus($"Проверка подсетей… {p:P0}"));
				result = await Task.Run(() => DeckDiscovery.ScanAsync(networks, progress, m_lifetime.Token));
			}

			if (result.BlockedByFirewall)
			{
				SetStatus("Поиск заблокирован: локальную сеть режет VPN");
				await Dialogs.Error(this, NetworkDiagnostics.VpnAdvice(null));
				return;
			}

			if (result.Decks.Count == 0)
			{
				SetStatus("Deck не найден");
				await Dialogs.Info(this,
					"Ничего не найдено.\n\nПроверьте, что на Deck включён Developer Mode (Настройки → Система) и он в той же сети, что и ПК. " +
					"Если сеть изолирует устройства друг от друга, добавьте Deck вручную по IP (Настройки → Интернет на Deck) или подключите его проводом.");
				return;
			}

			DeckDevice? first = null;
			foreach (DiscoveredDeck deck in result.Decks)
			{
				DeckDevice? existing = m_settings.Devices.FirstOrDefault(d =>
					string.Equals(d.Host, deck.Address, StringComparison.OrdinalIgnoreCase) ||
					string.Equals(d.Name, deck.Name, StringComparison.OrdinalIgnoreCase));

				if (existing == null)
				{
					existing = new DeckDevice { Name = deck.Name };
					m_settings.Devices.Add(existing);
				}

				existing.Host = deck.Address;
				existing.DevkitPort = deck.HttpPort;
				if (!string.IsNullOrWhiteSpace(deck.Login))
				{
					existing.User = deck.Login;
				}

				first ??= existing;
			}

			m_settings.Save();
			ReloadDevices(first);
			SetStatus($"Найдено устройств: {result.Decks.Count}");
		}
		catch (Exception e)
		{
			SetStatus("Ошибка поиска: " + e.Message);
		}
		finally
		{
			SetBusy(false);
		}
	}

	private async Task AddDeviceAsync()
	{
		var device = new DeckDevice();
		if (!await Dialogs.EditDevice(this, device, "Новый Deck"))
		{
			return;
		}

		m_settings.Devices.Add(device);
		m_settings.Save();
		ReloadDevices(device);
	}

	private async Task EditDeviceAsync()
	{
		DeckDevice? device = SelectedDevice;
		if (device == null)
		{
			return;
		}

		string oldHost = device.Host;
		if (!await Dialogs.EditDevice(this, device, "Изменить Deck"))
		{
			return;
		}

		// Новый адрес может оказаться другим устройством — отпечаток запомним заново
		if (!string.Equals(oldHost, device.Host, StringComparison.OrdinalIgnoreCase))
		{
			device.HostKeyFingerprint = null;
			Disconnect();
		}

		m_settings.Save();
		ReloadDevices(device);
	}

	private async Task RemoveDeviceAsync()
	{
		DeckDevice? device = SelectedDevice;
		if (device == null || !await Dialogs.YesNo(this, $"Удалить «{device.Name}» из списка?", "Удаление"))
		{
			return;
		}

		if (m_deck?.Device == device)
		{
			Disconnect();
		}

		m_settings.Devices.Remove(device);
		m_settings.Save();
		ReloadDevices();
	}

	// ---------------------------------------------------------------- сопряжение и подключение

	private async Task PairAsync()
	{
		DeckDevice? device = SelectedDevice;
		if (device == null)
		{
			await Dialogs.Info(this, "Сначала найдите Deck в сети или добавьте его вручную.");
			return;
		}

		SetBusy(true, $"Связь со службой devkit на {device.Host}:{device.DevkitPort}…");
		try
		{
			DevkitProperties? properties = await DevkitService.GetPropertiesAsync(device.Host, device.DevkitPort, m_lifetime.Token);
			if (properties == null)
			{
				if (await NetworkDiagnostics.IsBlockedAsync(device.Host, device.DevkitPort, m_lifetime.Token))
				{
					SetStatus("Соединение заблокировано: локальную сеть режет VPN");
					await Dialogs.Error(this, NetworkDiagnostics.VpnAdvice(device.Host));
					return;
				}

				SetStatus("Служба devkit не отвечает");
				await Dialogs.Error(this,
					$"Служба devkit не отвечает на {device.Host}:{device.DevkitPort}.\n\n" +
					"Включите на Deck: Настройки → Система → Enable Developer Mode. Если режим уже включён, проверьте адрес или используйте «Ключ по паролю…».");
				return;
			}

			if (!string.IsNullOrWhiteSpace(properties.Login))
			{
				device.User = properties.Login;
			}

			string publicKey = DeckKeys.EnsureKeyPair();
			SetStatus("Подтвердите сопряжение на экране Deck (Настройки → Developer → Pair new host)…");
			await DevkitService.RegisterAsync(device.Host, device.DevkitPort, publicKey, m_lifetime.Token);

			m_settings.Save();
			SetStatus("Сопряжение выполнено");
		}
		catch (Exception e) when (e is not OperationCanceledException)
		{
			SetStatus("Сопряжение не удалось");
			await Dialogs.Error(this, "Сопряжение не удалось: " + e.Message +
			                          "\n\nНа Deck откройте Настройки → Developer → Pair new host и подтвердите запрос, пока окно ожидания открыто.");
			return;
		}
		finally
		{
			SetBusy(false);
		}

		Disconnect();
		await ConnectAsync(quiet: false);
	}

	private async Task InstallKeyWithPasswordAsync()
	{
		DeckDevice? device = SelectedDevice;
		if (device == null)
		{
			await Dialogs.Info(this, "Сначала найдите Deck в сети или добавьте его вручную.");
			return;
		}

		string? password = await Dialogs.Prompt(this, "Ключ по паролю",
			$"Пароль пользователя {device.User} на Deck (задаётся командой passwd в Konsole). " +
			"Нужен один раз, чтобы записать SSH-ключ приложения; пароль не сохраняется.\n\n" +
			"На Deck должен работать SSH: sudo systemctl enable --now sshd", password: true);
		if (string.IsNullOrEmpty(password))
		{
			return;
		}

		SetBusy(true, "Установка ключа…");
		try
		{
			string publicKey = DeckKeys.EnsureKeyPair();
			string fingerprint = await DeckConnection.InstallKeyWithPasswordAsync(device, password, publicKey, m_lifetime.Token);
			device.HostKeyFingerprint ??= fingerprint;
			m_settings.Save();
			SetStatus("Ключ установлен");
		}
		catch (Exception e) when (e is not OperationCanceledException)
		{
			SetStatus("Ключ не установлен");
			await Dialogs.Error(this, DescribeConnectionError(e, device));
			return;
		}
		finally
		{
			SetBusy(false);
		}

		Disconnect();
		await ConnectAsync(quiet: false);
	}

	private async Task ToggleConnectionAsync()
	{
		if (m_deck != null)
		{
			Disconnect();
			return;
		}

		await ConnectAsync(quiet: false);
	}

	private async Task ConnectAsync(bool quiet)
	{
		DeckDevice? device = SelectedDevice;
		if (device == null)
		{
			if (!quiet)
			{
				await Dialogs.Info(this, "Сначала найдите Deck в сети или добавьте его вручную.");
			}
			return;
		}

		SetBusy(true, $"Подключение к {device.User}@{device.Host}…");
		m_btnConnect.IsEnabled = false;
		try
		{
			DeckConnection deck = await ConnectWithHostKeyCheckAsync(device);
			m_deck = deck;
			m_settings.Save();
			SetStatus($"Подключено к {device.Host}");
		}
		catch (Exception e) when (e is not OperationCanceledException)
		{
			SetStatus(NetworkDiagnostics.IsBlockedByFirewall(e)
				? "Не подключено: локальную сеть блокирует VPN (подробности — кнопка «Подключить»)"
				: "Не подключено: " + e.Message);
			if (!quiet)
			{
				await Dialogs.Error(this, DescribeConnectionError(e, device));
			}
		}
		finally
		{
			m_btnConnect.IsEnabled = true;
			SetBusy(false);
			UpdateConnectionState();
		}

		if (m_deck != null)
		{
			await OnConnectedAsync();
		}
	}

	private async Task<DeckConnection> ConnectWithHostKeyCheckAsync(DeckDevice device)
	{
		try
		{
			DeckConnection deck = await DeckConnection.ConnectAsync(device, m_lifetime.Token);
			device.HostKeyFingerprint ??= deck.HostKeyFingerprint;
			return deck;
		}
		catch (HostKeyMismatchException e)
		{
			bool trust = await Dialogs.YesNo(this,
				e.Message + "\n\nТак бывает после переустановки SteamOS или если по этому адресу теперь другое устройство. Доверять новому ключу?",
				"Ключ хоста изменился");
			if (!trust)
			{
				throw;
			}

			device.HostKeyFingerprint = e.Actual;
			return await DeckConnection.ConnectAsync(device, m_lifetime.Token);
		}
	}

	private void Disconnect()
	{
		if (m_deck == null)
		{
			return;
		}

		StopLogTail();
		m_relay.Stop();
		m_deck.Dispose();
		m_deck = null;
		SetStatus("Отключено");
		UpdateConnectionState();
	}

	private async Task OnConnectedAsync()
	{
		await RefreshStatusAsync();
		await NavigateFilesAsync(m_currentRemotePath ?? "~");
	}

	private void UpdateConnectionState()
	{
		bool connected = m_deck != null;
		m_btnConnect.Content = connected ? "Отключить" : "Подключить";
		m_connectionText.Text = connected ? "● подключено" : "○ не подключено";
		m_connectionText.Foreground = connected ? Ui.OkBrush : Ui.MutedBrush;
		if (!connected)
		{
			ClearStatusView();
		}
	}

	private static string DescribeConnectionError(Exception e, DeckDevice device)
	{
		return e switch
		{
			_ when NetworkDiagnostics.IsBlockedByFirewall(e) => NetworkDiagnostics.VpnAdvice(device.Host),
			SshAuthenticationException =>
				$"Deck ({device.User}@{device.Host}) не принял SSH-ключ.\n\n" +
				"Выполните «Сопрячь (Developer Mode)» или «Ключ по паролю…».",
			SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
				$"SSH на {device.Host}:{device.SshPort} не отвечает (соединение отклонено).\n\n" +
				"Сопрягите Deck через Developer Mode или включите SSH в Konsole: sudo systemctl enable --now sshd",
			SocketException or SshOperationTimeoutException =>
				$"Не удалось связаться с {device.Host}: {e.Message}\n\nПроверьте адрес и что Deck не спит и в той же сети.",
			_ => e.Message
		};
	}

	// ---------------------------------------------------------------- общие помощники

	// Выполняет операцию над подключённым Deck в фоне; ошибки показываются пользователю
	private async Task<bool> RunOnDeckAsync(string busyText, Func<DeckConnection, CancellationToken, Task> work, CancellationToken? token = null)
	{
		DeckConnection? deck = m_deck;
		if (deck == null)
		{
			await Dialogs.Info(this, "Нет подключения к Deck.");
			return false;
		}

		CancellationToken ct = token ?? m_lifetime.Token;
		SetBusy(true, busyText);
		try
		{
			await Task.Run(() => work(deck, ct), CancellationToken.None);

			// Иначе в строке состояния так и висело бы «…» уже завершённой операции
			if (m_statusText.Text == busyText)
			{
				SetStatus("Готово");
			}

			return true;
		}
		catch (OperationCanceledException)
		{
			SetStatus("Отменено");
			return false;
		}
		catch (Exception e)
		{
			SetStatus(e.Message);
			if (!deck.IsConnected)
			{
				// Частый сценарий: VPN включили посреди работы, и его kill switch оборвал соединение
				bool blocked = NetworkDiagnostics.IsBlockedByFirewall(e) ||
				               await NetworkDiagnostics.IsBlockedAsync(deck.Device.Host, deck.Device.SshPort, CancellationToken.None);
				Disconnect();
				await Dialogs.Error(this, "Соединение с Deck потеряно: " + e.Message +
				                          (blocked ? "\n\n" + NetworkDiagnostics.VpnAdvice(deck.Device.Host) : string.Empty));
			}
			else
			{
				await Dialogs.Error(this, e.Message);
			}
			return false;
		}
		finally
		{
			SetBusy(false);
		}
	}

	private void SetBusy(bool busy, string? status = null)
	{
		m_busyCount = Math.Max(0, m_busyCount + (busy ? 1 : -1));
		m_statusBusy.IsVisible = m_busyCount > 0;
		if (status != null)
		{
			SetStatus(status);
		}
	}

	private void SetStatus(string text)
	{
		m_statusText.Text = text;
	}

	private void ChangeTheme(AppColorMode mode)
	{
		m_settings.ColorMode = mode;
		m_settings.Save();
		if (Application.Current != null)
		{
			Application.Current.RequestedThemeVariant = mode.ToThemeVariant();
		}
	}

	private static void OpenLocalFolder(string path)
	{
		try
		{
			Directory.CreateDirectory(path);
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
		}
		catch
		{
			// ignore
		}
	}

	private async Task CopyToClipboardAsync(string text)
	{
		if (Clipboard != null)
		{
			await Clipboard.SetTextAsync(text);
			SetStatus("Скопировано: " + text);
		}
	}

	private const string SetupHelpText =
		"1. На Deck: Настройки → Система → Enable Developer Mode.\n" +
		"2. Настройки → Developer → Pair new host — оставьте экран открытым.\n" +
		"3. Здесь: «Найти в сети» (или «Добавить…» по IP), затем «Сопрячь (Developer Mode)» и подтвердите запрос на Deck.\n\n" +
		"Без Developer Mode: в Konsole на Deck задайте пароль (passwd), включите SSH (sudo systemctl enable --now sshd) и нажмите «Ключ по паролю…».\n\n" +
		"Если Deck уже сопряжён официальным SteamOS Devkit Client, его ключ подхватывается автоматически — достаточно «Подключить».\n\n" +
		"Провод: USB-C хаб с Ethernet в ту же сеть, что и ПК, — быстрее и стабильнее Wi-Fi для заливки билдов.";
}

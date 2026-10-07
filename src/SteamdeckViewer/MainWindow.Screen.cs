using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SteamdeckViewer.Core;

namespace SteamdeckViewer;

// Вкладка «Экран»: Sunshine на Deck (установка, запуск, сопряжение) и Moonlight на ПК для картинки и управления
internal sealed partial class MainWindow
{
	private static readonly (int Width, int Height, string Name)[] Resolutions =
	[
		(1280, 800, "1280×800 (экран Deck)"),
		(1920, 1080, "1920×1080"),
		(2560, 1440, "2560×1440")
	];

	private static readonly int[] FpsOptions = [30, 60, 90];
	private static readonly int[] BitrateOptions = [10, 20, 40, 80];

	private readonly SelectableTextBlock m_sunshineText = new() { TextWrapping = TextWrapping.Wrap };
	private readonly SelectableTextBlock m_moonlightText = new() { TextWrapping = TextWrapping.Wrap };
	private readonly ComboBox m_cbResolution = new() { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = Resolutions.Select(r => r.Name).ToList() };
	private readonly ComboBox m_cbFps = new() { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = FpsOptions.Select(f => f + " кадров/с").ToList() };
	private readonly ComboBox m_cbBitrate = new() { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = BitrateOptions.Select(b => b + " Мбит/с").ToList() };
	private readonly CheckBox m_cbFullscreen = new() { Content = "Во весь экран" };
	private readonly CheckBox m_cbOverlay = new() { Content = "Показывать статистику стрима (задержка, кадры)" };
	private readonly CheckBox m_cbForceComposite = new() { Content = "Обход чёрного экрана в Game Mode (принудительная композиция gamescope)" };

	// Moonlight, открытый кнопкой «Открыть экран Deck». Пока он открыт (и ещё немного после закрытия — Moonlight
	// закрывается сам, когда Sunshine перезапускается), приложение следит за режимом Deck
	private readonly DispatcherTimer m_streamWatch = new() { Interval = TimeSpan.FromSeconds(3) };
	private Process? m_stream;
	private DeckScreenMode m_streamMode;
	private int m_modeChangedTicks;
	private bool m_streamSwitching;

	private SunshineStatus? m_sunshineStatus;

	// Последний скриншот в буфере обмена
	private Bitmap? m_clipboardShot;

	private readonly CheckBox m_cbStopSunshineOnExit = new() { Content = "Останавливать Sunshine на Deck при выходе из программы" };
	private Task? m_exitSunshineStop;

	private Control BuildScreenTab()
	{
		m_streamWatch.Tick += async (_, _) => await WatchStreamAsync();
		Closing += EndStreamOnExit;

		m_cbResolution.SelectedIndex = Math.Clamp(m_settings.StreamResolution, 0, Resolutions.Length - 1);
		m_cbFps.SelectedIndex = Math.Max(0, Array.IndexOf(FpsOptions, m_settings.StreamFps));
		m_cbBitrate.SelectedIndex = Math.Max(0, Array.IndexOf(BitrateOptions, m_settings.StreamBitrateMbps));
		m_cbFullscreen.IsChecked = m_settings.StreamFullscreen;
		m_cbOverlay.IsChecked = m_settings.StreamPerformanceOverlay;
		m_cbForceComposite.IsChecked = m_settings.ForceGamescopeComposite;
		m_cbStopSunshineOnExit.IsChecked = m_settings.StopSunshineOnExit;
		m_cbStopSunshineOnExit.IsCheckedChanged += (_, _) =>
		{
			m_settings.StopSunshineOnExit = m_cbStopSunshineOnExit.IsChecked == true;
			ScheduleSave();
		};

		m_cbResolution.SelectionChanged += (_, _) => SaveStreamOptions();
		m_cbFps.SelectionChanged += (_, _) => SaveStreamOptions();
		m_cbBitrate.SelectionChanged += (_, _) => SaveStreamOptions();
		m_cbFullscreen.IsCheckedChanged += (_, _) => SaveStreamOptions();
		m_cbOverlay.IsCheckedChanged += (_, _) => SaveStreamOptions();
		m_cbForceComposite.IsCheckedChanged += (_, _) => SaveStreamOptions();

		Button open = Ui.Button("Открыть экран Deck", OpenScreenAsync);
		open.FontSize = 16;
		open.Padding = new Thickness(20, 8);

		var streamForm = new FormGrid();
		streamForm.Add("Разрешение", m_cbResolution);
		streamForm.Add("Частота кадров", m_cbFps);
		streamForm.Add("Битрейт", m_cbBitrate);
		streamForm.AddFull(m_cbFullscreen);
		streamForm.AddFull(m_cbOverlay);
		streamForm.AddFull(m_cbForceComposite);

		var streamGroup = Ui.Group("Стрим", Ui.Column(10,
			open,
			streamForm.Grid,
			Ui.Hint("Мышь и клавиатура ПК управляют Deck напрямую, геймпад, подключённый к ПК, работает как геймпад Deck. " +
			        "На рабочем столе курсор ПК совпадает с курсором Deck. В Game Mode мышь работает как в играх: щёлкните в окно стрима, " +
			        "чтобы захватить её, Ctrl+Alt+Shift+Z — отпустить (абсолютную мышь gamescope пока не понимает). " +
			        "Ctrl+Alt+Shift+X — окно или полный экран, Ctrl+Alt+Shift+Q — завершить стрим. При включённом VPN добавьте Moonlight.exe в исключения раздельного туннелирования. " +
			        "При выходе из программы окно стрима закрывается.")));

		var sunshineGroup = Ui.Group("Sunshine на Deck", Ui.Column(10,
			m_sunshineText,
			Ui.Row(
				Ui.Button("Обновить", RefreshSunshineAsync),
				Ui.Button("Установить…", InstallSunshineAsync),
				Ui.Button("Запустить", StartSunshineAsync),
				Ui.Button("Остановить", StopSunshineAsync),
				Ui.Button("Удалить…", UninstallSunshineAsync),
				Ui.Button("Веб-интерфейс", OpenSunshineWebUiAsync)),
			m_cbStopSunshineOnExit,
			Ui.Hint("Game Mode снимается только KMS-захватом, а он требует прав root. Поэтому для него Sunshine запускается так же, как в плагине " +
			        "decky-sunshine: flatpak от root, служба sdv-sunshine и правило polkit, чтобы запускать и останавливать её без пароля. " +
			        "Рабочий стол KMS-захват отдаёт повёрнутым, поэтому там Sunshine работает пользовательской службой и снимает экран через KDE. " +
			        "Приложение само запускает нужный вариант и переподключает экран, если режим Deck сменился во время стрима. " +
			        "Пароль sudo нужен только для установки и удаления и не сохраняется.")));

		var moonlightGroup = Ui.Group("Moonlight на ПК", Ui.Column(10,
			m_moonlightText,
			Ui.Row(
				Ui.Button("Указать Moonlight.exe…", BrowseMoonlightAsync),
				Ui.Button("Скачать Moonlight", () => Launcher.LaunchUriAsync(new Uri(Moonlight.DownloadUrl))),
				Ui.Button("Сопрячь с Deck", () => PairMoonlightAsync(quiet: false)))));

		var screenshotGroup = Ui.Group("Скриншот", Ui.Column(10,
			Ui.Row(Ui.Button("Сделать скриншот", TakeScreenshotAsync), Ui.Button("Открыть папку", () => OpenLocalFolder(ScreenshotFolder))),
			Ui.Hint("Снимок экрана Deck сохраняется на ПК в папку «Скриншоты» рядом с папками обмена и копируется в буфер обмена — " +
			        "его можно сразу вставить в задачу или чат. Moonlight не нужен. В Game Mode снимает gamescope: игра вместе с оверлеями " +
			        "(MangoHud, Steam) так, как на экране; на рабочем столе — spectacle. Та же кнопка есть на вкладке «Билды».")));

		UpdateMoonlightText();
		ShowSunshineStatus(null);

		return new ScrollViewer
		{
			Margin = new Thickness(0, 10, 0, 10),
			Content = Ui.Column(10, streamGroup, screenshotGroup, sunshineGroup, moonlightGroup)
		};
	}

	// ---------------------------------------------------------------- скриншот

	private string ScreenshotFolder => Path.Combine(ExchangeRoot, "Скриншоты");

	private async Task TakeScreenshotAsync()
	{
		string folder = ScreenshotFolder;
		string path = Path.Combine(folder, DeckScreenshot.FileName(DateTime.Now));
		if (!await RunOnDeckAsync("Скриншот Deck…", (deck, ct) =>
		    {
			    Directory.CreateDirectory(folder);
			    return DeckScreenshot.TakeAsync(deck, path, ct);
		    }))
		{
			return;
		}

		bool copied = false;
		if (Clipboard != null)
		{
			try
			{
				// Windows может забрать картинку из буфера позже, при вставке, поэтому Bitmap живёт до следующего снимка
				var bitmap = new Bitmap(path);
				await Clipboard.SetBitmapAsync(bitmap);
				m_clipboardShot?.Dispose();
				m_clipboardShot = bitmap;
				copied = true;
			}
			catch (Exception e)
			{
				ErrorLog.Write(e);
			}
		}

		SetStatus($"Скриншот сохранён{(copied ? " и скопирован в буфер обмена" : string.Empty)}: {path}");
	}

	private void SaveStreamOptions()
	{
		m_settings.StreamResolution = Math.Max(0, m_cbResolution.SelectedIndex);
		m_settings.StreamFps = FpsOptions[Math.Max(0, m_cbFps.SelectedIndex)];
		m_settings.StreamBitrateMbps = BitrateOptions[Math.Max(0, m_cbBitrate.SelectedIndex)];
		m_settings.StreamFullscreen = m_cbFullscreen.IsChecked == true;
		m_settings.StreamPerformanceOverlay = m_cbOverlay.IsChecked == true;
		m_settings.ForceGamescopeComposite = m_cbForceComposite.IsChecked == true;
		ScheduleSave();
	}

	// gamescope не пересчитывает координаты абсолютной мыши Sunshine (0..65535) в размер экрана, и курсор застревает
	// в углу (ValveSoftware/gamescope#2458). Поэтому в Game Mode мышь относительная, как в играх
	private StreamOptions CurrentStreamOptions(DeckScreenMode mode)
	{
		(int width, int height, _) = Resolutions[Math.Clamp(m_settings.StreamResolution, 0, Resolutions.Length - 1)];
		return new StreamOptions(width, height, m_settings.StreamFps, m_settings.StreamBitrateMbps * 1000,
			m_settings.StreamFullscreen, m_settings.StreamPerformanceOverlay, AbsoluteMouse: mode == DeckScreenMode.Desktop);
	}

	// ---------------------------------------------------------------- состояние

	private async Task RefreshSunshineAsync()
	{
		DeckConnection? deck = m_deck;
		if (deck == null)
		{
			ShowSunshineStatus(null);
			return;
		}

		SunshineStatus? status = null;
		if (await RunOnDeckAsync("Проверка Sunshine…", async (d, ct) => status = await SunshineHost.QueryAsync(d, ct)))
		{
			ShowSunshineStatus(status);
		}
	}

	private void ShowSunshineStatus(SunshineStatus? status)
	{
		m_sunshineStatus = status;
		string version = status?.Version != null ? $", версия {status.Version}" : string.Empty;
		string credentials = m_deck?.Device.Sunshine != null ? string.Empty : " Логин веб-интерфейса неизвестен этому ПК — переустановите Sunshine.";

		m_sunshineText.Text = status switch
		{
			null => "Состояние неизвестно: подключитесь к Deck и нажмите «Обновить».",
			{ ForeignInstance: true } => "Sunshine уже запущен, но не службой этого приложения (например, плагином decky-sunshine). " +
			                             "Остановите его или удалите плагин, затем нажмите «Установить…».",
			{ ServiceInstalled: false } => status.AppInstalled
				? $"Sunshine есть на Deck{version}, но служба приложения не настроена. Нажмите «Установить…»."
				: "Не установлен. Нажмите «Установить…» — понадобится пароль пользователя Deck.",
			{ NeedsUpgrade: true } => $"Установлен{version}, но установку нужно обновить: без этого рабочий стол в стриме повёрнут. " +
			                          "Нажмите «Установить…» — сопряжение с Moonlight сохранится.",
			{ Running: true } => $"Запущен для Game Mode (захват KMS){version}." + credentials,
			{ DesktopRunning: true } => $"Запущен для рабочего стола (захват через KDE){version}." + credentials,
			_ => $"Установлен, остановлен{version}." + credentials
		};
	}

	private void UpdateMoonlightText()
	{
		string? exe = Moonlight.FindExecutable(m_settings.MoonlightPath);
		m_moonlightText.Text = exe != null
			? "Найден: " + exe
			: "Не найден. Установите Moonlight (кнопка «Скачать Moonlight») или укажите путь к Moonlight.exe.";
	}

	// ---------------------------------------------------------------- Sunshine

	private async Task InstallSunshineAsync()
	{
		DeckConnection? deck = m_deck;
		if (deck == null)
		{
			await Dialogs.Info(this, "Нет подключения к Deck.");
			return;
		}

		if (!await Dialogs.YesNo(this,
			    "На Deck будет выполнено от root:\n" +
			    "• установка flatpak Sunshine (system, из Flathub, несколько сотен МБ);\n" +
			    "• служба /etc/systemd/system/sdv-sunshine.service для Game Mode: запускает Sunshine от root с setuid-копией bwrap — без этого не работает KMS-захват;\n" +
			    "• правило polkit: пользователь Deck сможет запускать и останавливать только эту службу без пароля;\n" +
			    "• правило udev: доступ пользователя Deck к виртуальным мыши, клавиатуре и геймпаду Sunshine — для рабочего стола;\n" +
			    "• настройки Sunshine (VAAPI) и новый логин веб-интерфейса; настройки, ключи и сопряжения автоматически копируются " +
			    "между Game Mode и рабочим столом (скрипт /var/lib/steamdeckviewer/sync-config.sh).\n\n" +
			    "Для рабочего стола Sunshine запускается без root, пользовательской службой, при первом стриме с рабочего стола.\n\n" +
			    "Всё это переживает обновления SteamOS и убирается кнопкой «Удалить…». Если Sunshine уже настроен плагином decky-sunshine, его логин будет заменён.\n\n" +
			    "Продолжить?", "Установка Sunshine"))
		{
			return;
		}

		string? password = await AskSudoPasswordAsync(deck.Device);
		if (password == null)
		{
			return;
		}

		SunshineCredentials credentials = SunshineCredentials.Generate();
		if (await RunOnDeckAsync("Установка Sunshine на Deck — это может занять несколько минут…",
			    (d, ct) => SunshineHost.InstallAsync(d, password, credentials, ct)))
		{
			deck.Device.Sunshine = credentials;
			m_settings.Save();
			SetStatus("Sunshine установлен");
		}

		await RefreshSunshineAsync();
	}

	private async Task UninstallSunshineAsync()
	{
		DeckConnection? deck = m_deck;
		if (deck == null)
		{
			await Dialogs.Info(this, "Нет подключения к Deck.");
			return;
		}

		if (!await Dialogs.YesNo(this, "Удалить с Deck службы Sunshine, правила polkit и udev и flatpak Sunshine вместе с его настройками и сопряжениями?", "Удаление Sunshine"))
		{
			return;
		}

		string? password = await AskSudoPasswordAsync(deck.Device);
		if (password == null)
		{
			return;
		}

		if (await RunOnDeckAsync("Удаление Sunshine…", (d, ct) => SunshineHost.UninstallAsync(d, password, ct)))
		{
			deck.Device.Sunshine = null;
			m_settings.Save();
			SetStatus("Sunshine удалён");
		}

		await RefreshSunshineAsync();
	}

	private async Task<string?> AskSudoPasswordAsync(DeckDevice device)
	{
		string? password = await Dialogs.Prompt(this, "Пароль sudo",
			$"Пароль пользователя {device.User} на Deck (тот, что задаётся командой passwd). Он передаётся в sudo один раз и не сохраняется.",
			password: true);
		return string.IsNullOrEmpty(password) ? null : password;
	}

	private async Task StartSunshineAsync()
	{
		bool force = m_settings.ForceGamescopeComposite;
		SunshineStatus? status = null;
		DeckScreenMode mode = DeckScreenMode.Unknown;
		if (await RunOnDeckAsync("Запуск Sunshine…", async (d, ct) =>
		    {
			    status = await SunshineHost.QueryAsync(d, ct);
			    if (!status.NeedsUpgrade)
			    {
				    mode = await SunshineHost.StartAsync(d, force, ct);
			    }
		    }))
		{
			if (await ReportUpgradeNeededAsync(status))
			{
				return;
			}

			SetStatus("Sunshine запущен: " + ModeName(mode));
		}

		await RefreshSunshineAsync();
	}

	private async Task<bool> ReportUpgradeNeededAsync(SunshineStatus? status)
	{
		if (status is not { NeedsUpgrade: true })
		{
			return false;
		}

		ShowSunshineStatus(status);
		await Dialogs.Info(this, "Установку Sunshine на Deck нужно обновить: для рабочего стола теперь запускается отдельная служба, " +
		                         "которая снимает экран в правильной ориентации. Нажмите «Установить…» в блоке «Sunshine на Deck» — " +
		                         "понадобится пароль sudo, сопряжение с Moonlight сохранится.");
		return true;
	}

	private static string ModeName(DeckScreenMode mode)
	{
		return mode == DeckScreenMode.Desktop ? "рабочий стол" : "Game Mode";
	}

	private static string MouseHint(DeckScreenMode mode)
	{
		return mode == DeckScreenMode.Desktop ? string.Empty : " — щёлкните в окно стрима, чтобы захватить мышь, Ctrl+Alt+Shift+Z — отпустить";
	}

	private async Task StopSunshineAsync()
	{
		if (await RunOnDeckAsync("Остановка Sunshine…", SunshineHost.StopAsync))
		{
			SetStatus("Sunshine остановлен");
		}

		await RefreshSunshineAsync();
	}

	private async Task OpenSunshineWebUiAsync()
	{
		DeckConnection? deck = m_deck;
		if (deck == null)
		{
			await Dialogs.Info(this, "Нет подключения к Deck.");
			return;
		}

		if (deck.Device.Sunshine is { } credentials)
		{
			await CopyToClipboardAsync(credentials.Password);
			await Dialogs.Info(this, $"Логин веб-интерфейса: {credentials.User}\nПароль скопирован в буфер обмена.\n\n" +
			                         "Браузер предупредит о самоподписанном сертификате Sunshine — это ожидаемо.", "Веб-интерфейс Sunshine");
		}

		await Launcher.LaunchUriAsync(new Uri($"https://{deck.Device.Host}:{SunshineHost.WebPort}"));
	}

	// ---------------------------------------------------------------- Moonlight

	private async Task BrowseMoonlightAsync()
	{
		IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			AllowMultiple = false,
			Title = "Moonlight.exe",
			FileTypeFilter = [new FilePickerFileType("Moonlight") { Patterns = ["Moonlight.exe", "*.exe"] }]
		});
		string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
		if (path != null)
		{
			m_settings.MoonlightPath = path;
			m_settings.Save();
			UpdateMoonlightText();
		}
	}

	// Moonlight запрашивает сопряжение со своим PIN, а мы подтверждаем тот же PIN в Sunshine через API
	private async Task<bool> PairMoonlightAsync(bool quiet)
	{
		if (!await EnsureStreamPrerequisitesAsync() || m_deck is not { Device.Sunshine: { } credentials } deck)
		{
			return false;
		}

		string exe = Moonlight.FindExecutable(m_settings.MoonlightPath)!;
		string host = deck.Device.Host;
		string clientName = Moonlight.ClientName();
		string pin = Moonlight.NewPin();
		bool paired = false;

		bool ok = await RunOnDeckAsync("Сопряжение Moonlight с Sunshine…", async (d, ct) =>
		{
			// Sunshine 2026.9+ подтверждает PIN для конкретного ожидающего запроса; null — старая версия
			IReadOnlyList<SunshinePairing>? before = await SunshineHost.PendingPairingsAsync(d, credentials, ct);
			IReadOnlySet<string> local = NetworkDiagnostics.LocalAddresses();

			// Moonlight шлёт один и тот же uniqueid, и неотвеченный запрос прошлой попытки не даст начать новый
			foreach (SunshinePairing stale in (before ?? []).Where(p => local.Contains(p.Address)))
			{
				await SunshineHost.CancelPairingAsync(d, credentials, stale.Id, ct);
			}

			HashSet<string> known = (before ?? []).Select(p => p.Id).ToHashSet();

			using Process pairing = Moonlight.Start(exe, Moonlight.PairArguments(host, pin));
			for (int attempt = 0; attempt < 30 && !paired && !pairing.HasExited; attempt++)
			{
				await Task.Delay(1000, ct);
				if (before == null)
				{
					paired = await SunshineHost.SendPinAsync(d, credentials, null, pin, clientName, ct);
					continue;
				}

				IReadOnlyList<SunshinePairing> pending = await SunshineHost.PendingPairingsAsync(d, credentials, ct) ?? [];
				if (SunshineHost.PickPairing(pending, known, local) is { } request)
				{
					// Ответ приходит после обмена ключами; при неудаче запрос уже снят, и Moonlight сам покажет ошибку
					paired = await SunshineHost.SendPinAsync(d, credentials, request.Id, pin, clientName, ct);
					break;
				}
			}

			using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
			wait.CancelAfter(TimeSpan.FromSeconds(30));
			try
			{
				await pairing.WaitForExitAsync(wait.Token);
			}
			catch (OperationCanceledException) when (!ct.IsCancellationRequested)
			{
				// Moonlight всё ещё показывает окно — оставляем его пользователю
			}

			paired = (await SunshineHost.PairedClientsAsync(d, credentials, ct)).Contains(clientName);
		});

		if (ok && paired)
		{
			SetStatus("Moonlight сопряжён с Deck");
			if (!quiet)
			{
				await Dialogs.Info(this, "Moonlight сопряжён с Sunshine на Deck. Теперь «Открыть экран Deck» работает в одно нажатие.");
			}
		}
		else if (ok)
		{
			await Dialogs.Error(this,
				"Sunshine не подтвердил сопряжение. Проверьте, что Moonlight видит Deck по адресу " + host +
				" (при включённом VPN добавьте Moonlight.exe в исключения) и что окно Moonlight не показывает ошибку.");
		}

		return ok && paired;
	}

	private async Task OpenScreenAsync()
	{
		if (!await EnsureStreamPrerequisitesAsync() || m_deck is not { Device.Sunshine: { } credentials } deck)
		{
			return;
		}

		bool force = m_settings.ForceGamescopeComposite;
		bool paired = false;
		SunshineStatus? status = null;
		DeckScreenMode mode = DeckScreenMode.Unknown;
		if (!await RunOnDeckAsync("Подготовка стрима…", async (d, ct) =>
		    {
			    status = await SunshineHost.QueryAsync(d, ct);
			    if (status.NeedsUpgrade)
			    {
				    return;
			    }

			    // Запускает вариант под текущий режим Deck; если нужный уже работает, ничего не меняет
			    mode = await SunshineHost.StartAsync(d, force, ct);
			    paired = (await SunshineHost.PairedClientsAsync(d, credentials, ct)).Contains(Moonlight.ClientName());
		    }))
		{
			return;
		}

		if (await ReportUpgradeNeededAsync(status))
		{
			return;
		}

		if (!paired && !await PairMoonlightAsync(quiet: true))
		{
			return;
		}

		try
		{
			StartStream(deck, mode);
			SetStatus("Moonlight запущен: " + ModeName(mode) + MouseHint(mode));
		}
		catch (Exception e)
		{
			await Dialogs.Error(this, "Не удалось запустить Moonlight: " + e.Message);
		}

		await RefreshSunshineAsync();
	}

	private void StartStream(DeckConnection deck, DeckScreenMode mode)
	{
		string exe = Moonlight.FindExecutable(m_settings.MoonlightPath) ?? throw new InvalidOperationException("Moonlight.exe не найден.");

		// Повторное «Открыть экран»: второе окно Moonlight всё равно выбило бы первое из стрима
		CloseStream();
		Process stream = Moonlight.Start(exe, Moonlight.StreamArguments(deck.Device.Host, CurrentStreamOptions(mode)));
		m_stream = stream;
		m_streamMode = mode;
		m_modeChangedTicks = 0;
		m_streamWatch.Start();
	}

	// Deck переключили между Game Mode и рабочим столом во время стрима: запускаем Sunshine под новый режим и открываем
	// Moonlight заново. Новый режим должен продержаться две проверки подряд: пока сеанс сменяется, на экране никого нет
	private async Task WatchStreamAsync()
	{
		if (m_streamSwitching)
		{
			return;
		}

		Process? stream = m_stream;
		bool recentlyClosed = stream is { HasExited: true } && DateTime.Now - stream.ExitTime < TimeSpan.FromSeconds(30);
		if (stream == null || stream.HasExited && !recentlyClosed || m_deck is not { } deck)
		{
			m_streamWatch.Stop();
			return;
		}

		m_streamSwitching = true;
		try
		{
			SunshineStatus status = await Task.Run(() => SunshineHost.QueryAsync(deck, m_lifetime.Token));
			bool changed = status.Session != DeckScreenMode.Unknown && (status.Session != m_streamMode || status.WrongInstance);
			m_modeChangedTicks = changed ? m_modeChangedTicks + 1 : 0;
			if (m_modeChangedTicks < 2)
			{
				return;
			}

			SetStatus($"Deck переключён на {ModeName(status.Session)}: перезапуск Sunshine…");
			bool force = m_settings.ForceGamescopeComposite;
			DeckScreenMode mode = await Task.Run(() => SunshineHost.StartAsync(deck, force, m_lifetime.Token));

			StartStream(deck, mode);
			SetStatus("Экран Deck переподключён: " + ModeName(mode) + MouseHint(mode));
			ShowSunshineStatus(await Task.Run(() => SunshineHost.QueryAsync(deck, m_lifetime.Token)));
		}
		catch (OperationCanceledException)
		{
			// окно закрывается
		}
		catch (Exception e)
		{
			// Не повторяем каждые 3 секунды: экран можно открыть заново кнопкой
			m_streamWatch.Stop();
			SetStatus("Не удалось переподключить экран после смены режима Deck: " + e.Message);
		}
		finally
		{
			m_streamSwitching = false;
		}
	}

	// Выход из программы заканчивает трансляцию: окно Moonlight, открытое программой, закрывается, а Sunshine на Deck
	// по галке останавливается. Закрытие окна ждёт остановку не дольше 5 с, чтобы без сети программа не висела
	private async void EndStreamOnExit(object? sender, WindowClosingEventArgs e)
	{
		if (m_exitSunshineStop != null)
		{
			// Повторное закрытие, пока Sunshine останавливается, ждёт остановку
			e.Cancel = !m_exitSunshineStop.IsCompleted;
			return;
		}

		m_streamWatch.Stop();
		bool streamed = m_stream != null;
		CloseStream();

		// При выключении Windows не задерживаем систему ради Deck
		if (!m_settings.StopSunshineOnExit || e.CloseReason == WindowCloseReason.OSShutdown || m_deck is not { } deck ||
		    !streamed && m_sunshineStatus is not { AnyRunning: true })
		{
			return;
		}

		e.Cancel = true;
		IsEnabled = false;
		SetStatus("Остановка Sunshine на Deck…");
		m_exitSunshineStop = Task.Run(async () =>
		{
			try
			{
				using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
				await SunshineHost.StopAsync(deck, timeout.Token);
			}
			catch (Exception ex)
			{
				ErrorLog.Write(ex);
			}
		});
		await m_exitSunshineStop;
		Close();
	}

	private void CloseStream()
	{
		if (m_stream == null)
		{
			return;
		}

		try
		{
			if (!m_stream.HasExited)
			{
				m_stream.Kill();
				m_stream.WaitForExit(5000);
			}
		}
		catch (InvalidOperationException)
		{
			// Moonlight закрылся сам
		}

		m_stream.Dispose();
		m_stream = null;
	}

	private async Task<bool> EnsureStreamPrerequisitesAsync()
	{
		DeckConnection? deck = m_deck;
		if (deck == null)
		{
			await Dialogs.Info(this, "Нет подключения к Deck.");
			return false;
		}

		if (Moonlight.FindExecutable(m_settings.MoonlightPath) == null)
		{
			if (await Dialogs.YesNo(this, "Moonlight на ПК не найден. Открыть страницу загрузки?", "Moonlight"))
			{
				await Launcher.LaunchUriAsync(new Uri(Moonlight.DownloadUrl));
			}
			return false;
		}

		if (deck.Device.Sunshine == null)
		{
			await RefreshSunshineAsync();
			await Dialogs.Info(this, "Sunshine на Deck ещё не настроен этим приложением. Нажмите «Установить…» в блоке «Sunshine на Deck».");
			return false;
		}

		return true;
	}
}

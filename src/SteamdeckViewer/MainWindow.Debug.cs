using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SteamdeckViewer.Core;

namespace SteamdeckViewer;

// Вкладка «Отладка»: адрес и порт отладчика Unity-плеера на Deck, ретрансляция анонса для Rider и Unity Editor,
// запись Unity Profiler в файл на Deck, оверлей и замеры MangoHud
internal sealed partial class MainWindow
{
	private readonly UnityAnnounceRelay m_relay = new();
	private readonly SelectableTextBlock m_playerText = new() { TextWrapping = TextWrapping.Wrap, FontFamily = Ui.Mono };
	private readonly CheckBox m_cbRelay = new() { Content = "Показывать плеер в Rider и Unity Editor автоматически (повторять анонс плеера в сети ПК)" };
	private readonly CheckBox m_cbRecordProfiler = new() { Content = "Записывать профиль в файл на Deck при каждом запуске игры" };
	private readonly TextBox m_tbProfilerFrames = new() { Width = 140, PlaceholderText = "до закрытия игры" };
	private readonly CheckBox m_cbMangoHudOverlay = new() { Content = "Показывать оверлей MangoHud в игре" };
	private readonly CheckBox m_cbMangoHudLog = new() { Content = "Записывать замеры в CSV" };
	private readonly TextBox m_tbMangoHudSeconds = new() { Width = 140, PlaceholderText = "до закрытия игры" };
	private volatile UnityPlayerInfo? m_playerInfo;

	private Control BuildDebugTab()
	{
		m_cbRelay.IsCheckedChanged += (_, _) => UpdateDebugView();
		m_cbRecordProfiler.IsCheckedChanged += (_, _) => UpdateProfile(p => p.RecordProfiler = m_cbRecordProfiler.IsChecked == true);
		BindText(m_tbProfilerFrames, (p, v) => p.ProfilerFrameCount = int.TryParse(v.Trim(), out int frames) && frames > 0 ? frames : 0);
		m_cbMangoHudOverlay.IsCheckedChanged += (_, _) => UpdateProfile(p => p.MangoHudOverlay = m_cbMangoHudOverlay.IsChecked == true);
		m_cbMangoHudLog.IsCheckedChanged += (_, _) => UpdateProfile(p => p.MangoHudLog = m_cbMangoHudLog.IsChecked == true);
		BindText(m_tbMangoHudSeconds, (p, v) => p.MangoHudLogSeconds = int.TryParse(v.Trim(), out int seconds) && seconds > 0 ? seconds : 0);

		var playerGroup = Ui.Group("Unity-плеер на Deck", Ui.Column(10,
			m_playerText,
			Ui.Row(
				Ui.Button("Скопировать адрес:порт", CopyDebuggerAddressAsync),
				Ui.Button("Найти в Player.log", FindPlayerInLogAsync),
				Ui.Button("Следить за логом", StartLogTail)),
			m_cbRelay,
			Ui.Hint("Rider и Unity Editor (Profiler, Console → Attach to Player) ищут плееры по UDP-анонсам на 225.0.0.222:54997. " +
			        "По Wi-Fi multicast от Deck часто не доходит; с этой галочкой приложение раз в секунду повторяет анонс плеера " +
			        "с адресом Deck (экспериментально). Сведения о плеере берутся из Player.log, поэтому слежение за логом должно быть включено.")));

		var profilerGroup = Ui.Group("Unity Profiler", Ui.Column(10,
			new SelectableTextBlock
			{
				TextWrapping = TextWrapping.Wrap,
				Text =
					"Вживую: Window → Analysis → Profiler → Attach to Player → плеер Deck. Если его нет в списке — галочка анонса выше " +
					"или «<Enter IP>» с IP Deck. Нужна Development-сборка."
			},
			Ui.Row(Ui.Button("Скопировать IP для Profiler", CopyDeckIpAsync)),
			Ui.Note("Запись в файл — только для Development-билда, залитого через программу и запущенного её кнопками или из Unity. " +
			        "На игры из магазина Steam и другие ярлыки в библиотеке Deck не действует."),
			m_cbRecordProfiler,
			Ui.Row(new TextBlock { Text = "Записать кадров:", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, m_tbProfilerFrames),
			Ui.Row(
				Ui.Button("Скачать последнюю запись…", DownloadProfilerRecordingAsync),
				Ui.Button("Удалить записи на Deck", DeleteProfilerRecordingsAsync)),
			Ui.Hint("Запись в файл: игра стартует с -profiler-enable -profiler-log-file и пишет .raw на Deck с первого кадра, " +
			        "включая загрузку, а сеть на замеры не влияет. Без лимита кадров файл растёт, пока игра запущена, — закройте её перед скачиванием. " +
			        "Открыть запись: Unity → Profiler → Load.")));

		var mangoHudGroup = Ui.Group("Производительность Deck (MangoHud)", Ui.Column(10,
			Ui.Note("Галки действуют на билд, залитый через программу и запущенный её кнопками или из Unity. " +
			        "Для игры из магазина Steam — кнопка «Включить для игры из Steam» ниже."),
			m_cbMangoHudOverlay,
			m_cbMangoHudLog,
			Ui.Row(new TextBlock { Text = "Записать секунд:", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, m_tbMangoHudSeconds),
			Ui.Row(
				Ui.Button("Скачать последний замер…", DownloadMangoHudLogAsync),
				Ui.Button("Удалить замеры на Deck", DeleteMangoHudLogsAsync)),
			Ui.Row(
				Ui.Button("Включить для игры из Steam", () => SetSteamLaunchOptionsAsync(enable: true)),
				Ui.Button("Выключить для игры из Steam", () => SetSteamLaunchOptionsAsync(enable: false))),
			Ui.Hint("Игра из магазина Steam с AppID из профиля (поле «Steam AppID» на вкладке «Билды»): программа записывает в её параметры " +
			        "запуска строку «env MANGOHUD=1 MANGOHUD_CONFIG=… %command%» по галкам выше, замеры ложатся туда же. Записывает через " +
			        "CEF-отладку Steam (вкладка «Устройство»), без неё строка копируется в буфер обмена для ручной вставки. Параметры запуска " +
			        "заменяются целиком, прежние программа пишет в окно Player.log; «Выключить» их очищает."),
			Ui.Hint("MangoHud входит в SteamOS. Оверлей показывает FPS, время кадра с графиком, загрузку, температуры и мощность CPU/GPU, " +
			        "память и батарею; запись пишет то же в CSV на каждый кадр. Включается со следующего запуска, " +
			        "Development-сборка не нужна. После скачивания программа покажет итоги: средний FPS, 1% и 0,1% low, " +
			        "время кадра, загрузку, температуры. Profiler показывает, что происходит внутри игры, а MangoHud — как ведёт себя железо.")));

		var prepareGroup = Ui.Group("Подготовка билда", new SelectableTextBlock
		{
			TextWrapping = TextWrapping.Wrap,
			Text =
				"• Build/Street Build Settings: платформа Linux, включить Development и Script Debugging (IL2CPP поддерживается).\n" +
				"• Анти-отладка DD.cs закрывает игру при подключённом отладчике (TracerPid / Debugger.IsAttached), " +
				"если нет DEV_BUILD или define DISABLE_CHECK_DEFENCE.\n" +
				"• EAC выключен (Use Easy Anti-Cheat снят → EOS_DISABLE): запускается CarX_Street.x86_64 напрямую.\n" +
				"• По желанию: Wait For Managed Debugger — плеер подождёт подключения отладчика на старте.\n" +
				"• Обфускацию (Mfuscator) для отладочных билдов лучше выключить, иначе имена в отладчике будут нечитаемыми."
		});

		var riderGroup = Ui.Group("Подключение из Rider", new SelectableTextBlock
		{
			TextWrapping = TextWrapping.Wrap,
			Text =
				"1. Откройте решение игры в Rider и поставьте точки останова.\n" +
				"2. Run → Attach to Unity Process…\n" +
				"3. Выберите плеер из списка или нажмите «Add player address manually» и введите адрес и порт выше.\n" +
				"Брандмауэр Windows должен пропускать входящий UDP для Rider, а порт 56000–56999 на Deck — быть доступен по сети."
		});

		UpdateDebugView();

		return new ScrollViewer
		{
			Margin = new Thickness(0, 10, 0, 10),
			Content = Ui.Column(10, playerGroup, profilerGroup, mangoHudGroup, prepareGroup, riderGroup)
		};
	}

	private async Task CopyDeckIpAsync()
	{
		string? host = m_deck?.Device.Host ?? SelectedDevice?.Host;
		if (host == null)
		{
			await Dialogs.Info(this, "Сначала выберите Deck.");
			return;
		}

		await CopyToClipboardAsync(host);
		SetStatus($"Скопировано: {host} — вставьте в Profiler → <Enter IP>");
	}

	private async Task DownloadProfilerRecordingAsync()
	{
		BuildProfile profile = CurrentProfile;
		ProfilerRecording? latest = null;
		if (!await RunOnDeckAsync("Поиск записей профайлера…", async (deck, ct) => latest = await ProfilerCapture.FindLatestAsync(deck, profile, ct)))
		{
			return;
		}

		if (latest == null)
		{
			await Dialogs.Info(this, $"На Deck нет записей профайлера для «{profile.Name}». " +
			                         "Включите запись и запустите Development-сборку кнопкой программы.", "Unity Profiler");
			return;
		}

		IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = "Сохранить запись профайлера",
			SuggestedFileName = latest.Name,
			DefaultExtension = "raw"
		});
		string? localPath = file?.TryGetLocalPath();
		if (localPath == null)
		{
			return;
		}

		if (await RunOnDeckAsync($"Скачивание {latest.Name} ({DeckStatus.FormatBytes(latest.Size)})…", (deck, _) =>
		    {
			    using FileStream output = File.Create(localPath);
			    deck.GetSftp().DownloadFile(latest.RemotePath, output);
			    return Task.CompletedTask;
		    }))
		{
			SetStatus($"Запись сохранена: {localPath} — откройте её в Unity: Profiler → Load");
		}
	}

	private async Task DownloadMangoHudLogAsync()
	{
		BuildProfile profile = CurrentProfile;
		MangoHudLog? latest = null;
		if (!await RunOnDeckAsync("Поиск замеров MangoHud…", async (deck, ct) => latest = await MangoHudCapture.FindLatestAsync(deck, profile, ct)))
		{
			return;
		}

		if (latest == null)
		{
			await Dialogs.Info(this, $"На Deck нет замеров MangoHud для «{profile.Name}». " +
			                         "Включите запись замеров и запустите игру кнопкой программы.", "MangoHud");
			return;
		}

		IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = "Сохранить замер MangoHud",
			SuggestedFileName = latest.Name,
			DefaultExtension = "csv"
		});
		string? localPath = file?.TryGetLocalPath();
		if (localPath == null)
		{
			return;
		}

		if (!await RunOnDeckAsync($"Скачивание {latest.Name} ({DeckStatus.FormatBytes(latest.Size)})…", (deck, _) =>
		    {
			    using FileStream output = File.Create(localPath);
			    deck.GetSftp().DownloadFile(latest.RemotePath, output);
			    return Task.CompletedTask;
		    }))
		{
			return;
		}

		SetStatus("Замер сохранён: " + localPath);
		MangoHudSummary? summary = MangoHudCapture.Summarize(await File.ReadAllTextAsync(localPath));
		if (summary == null)
		{
			await Dialogs.Info(this, "Замер сохранён, но в нём нет ни одного кадра: игра закрылась сразу или запись ещё не началась.", "MangoHud");
			return;
		}

		string text = summary.Describe();
		m_playerLog.Append("[CarX Deck Tools] MangoHud, " + latest.Name + ":\n" + text);
		await Dialogs.Info(this, text + "\n\nФайл: " + localPath, "MangoHud");
	}

	// MangoHud для игры из магазина Steam: её запускает Steam, поэтому переменные идут через параметры запуска игры
	private async Task SetSteamLaunchOptionsAsync(bool enable)
	{
		BuildProfile profile = CurrentProfile;
		if (!uint.TryParse(profile.SteamAppId.Trim(), out uint appId))
		{
			await Dialogs.Info(this, "Укажите Steam AppID игры в профиле: вкладка «Билды», поле «Steam AppID» (у CarX Street — 1114150).");
			return;
		}

		if (enable && !profile.MangoHudOverlay && !profile.MangoHudLog)
		{
			await Dialogs.Info(this, "Отметьте «Показывать оверлей MangoHud в игре» или «Записывать замеры в CSV».");
			return;
		}

		string options = string.Empty;
		string? before = null;
		bool noCef = false;
		bool ok = await RunOnDeckAsync(enable ? "Запись параметров запуска в Steam…" : "Очистка параметров запуска в Steam…", async (deck, ct) =>
		{
			if (enable)
			{
				options = MangoHudCapture.SteamLaunchOptions((await MangoHudCapture.PrepareAsync(deck, profile, ct))!);
			}

			try
			{
				using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
				timeout.CancelAfter(TimeSpan.FromSeconds(20));
				before = await SteamCef.SetLaunchOptionsAsync(deck, appId, options, timeout.Token);
			}
			catch (SteamCefUnavailableException)
			{
				noCef = true;
			}
		});

		if (!ok)
		{
			return;
		}

		if (noCef)
		{
			if (enable)
			{
				await CopyToClipboardAsync(options);
			}

			await Dialogs.Info(this,
				"CEF-отладка Steam недоступна, поэтому параметры запуска программа изменить не может. Включите её на вкладке «Устройство» " +
				"(«Включить CEF-отладку Steam», затем «Перезапустить Steam») и повторите." +
				(enable ? "\n\nСтрока уже в буфере обмена, её можно вставить вручную: свойства игры → «Параметры запуска»:\n\n" + options : string.Empty),
				"Параметры запуска Steam");
			return;
		}

		if (!string.IsNullOrEmpty(before) && before != options)
		{
			m_playerLog.Append($"[CarX Deck Tools] прежние параметры запуска игры {appId} в Steam: {before}");
		}

		SetStatus(enable
			? $"MangoHud включён для игры {appId} из Steam — со следующего запуска"
			: $"Параметры запуска игры {appId} в Steam очищены");
	}

	private async Task DeleteMangoHudLogsAsync()
	{
		BuildProfile profile = CurrentProfile;
		if (!await Dialogs.YesNo(this, $"Удалить на Deck все замеры MangoHud для «{profile.Name}» ({MangoHudCapture.Folder(profile)})?", "MangoHud"))
		{
			return;
		}

		if (await RunOnDeckAsync("Удаление замеров MangoHud…", (deck, ct) => MangoHudCapture.DeleteAllAsync(deck, profile, ct)))
		{
			SetStatus("Замеры MangoHud на Deck удалены");
		}
	}

	private async Task DeleteProfilerRecordingsAsync()
	{
		BuildProfile profile = CurrentProfile;
		if (!await Dialogs.YesNo(this, $"Удалить на Deck все записи профайлера для «{profile.Name}» ({ProfilerCapture.Folder(profile)})?", "Unity Profiler"))
		{
			return;
		}

		if (await RunOnDeckAsync("Удаление записей профайлера…", (deck, ct) => ProfilerCapture.DeleteAllAsync(deck, profile, ct)))
		{
			SetStatus("Записи профайлера на Deck удалены");
		}
	}

	private void UpdateDebugView()
	{
		UnityPlayerInfo? info = m_playerInfo;
		string host = m_deck?.Device.Host ?? SelectedDevice?.Host ?? "—";

		if (info == null)
		{
			m_playerText.Text = "Плеер ещё не найден.\nЗапустите Development-билд и включите слежение за Player.log (вкладка «Билды») — адрес и порт появятся здесь.";
		}
		else
		{
			m_playerText.Text =
				$"Проект:          {info.ProjectName ?? "—"}\n" +
				$"IP из анонса:    {info.Ip ?? "—"}\n" +
				$"Порт отладчика:  {(info.DebuggerPort?.ToString() ?? "— (Script Debugging выключен?)")}\n" +
				$"Для Rider:       {host}:{info.DebuggerPort?.ToString() ?? "?"}";
		}

		bool relay = m_cbRelay.IsChecked == true && info?.Announce != null && m_deck != null;
		if (relay)
		{
			m_relay.Start(info!.Announce!, host);
		}
		else if (m_relay.IsRunning)
		{
			m_relay.Stop();
		}
	}

	private async Task CopyDebuggerAddressAsync()
	{
		UnityPlayerInfo? info = m_playerInfo;
		if (info?.DebuggerPort == null || m_deck == null)
		{
			await Dialogs.Info(this, "Порт отладчика ещё не известен: запустите Development-билд со Script Debugging и следите за Player.log.");
			return;
		}

		await CopyToClipboardAsync($"{m_deck.Device.Host}:{info.DebuggerPort}");
	}

	// Разовый поиск в уже записанном логе — если слежение включили после старта игры с ротацией лога
	private async Task FindPlayerInLogAsync()
	{
		string logPath = CurrentProfile.PlayerLogPath;
		string output = string.Empty;
		if (!await RunOnDeckAsync("Поиск в Player.log…", async (deck, ct) => output = (await deck.RunAsync(UnityPlayerLog.GrepCommand(logPath), ct)).Output))
		{
			return;
		}

		UnityPlayerInfo? info = null;
		foreach (string line in output.Split('\n'))
		{
			info = UnityPlayerLog.Parse(line, info) ?? info;
		}

		m_playerInfo = info;
		UpdateDebugView();
		SetStatus(info == null ? "В Player.log нет сведений о плеере" : "Сведения о плеере обновлены");
	}
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SteamdeckViewer.Core;

namespace SteamdeckViewer;

// Вкладка «Отладка»: адрес и порт отладчика Unity-плеера на Deck, ретрансляция анонса для Rider и Unity Editor,
// запись Unity Profiler в файл на Deck
internal sealed partial class MainWindow
{
	private readonly UnityAnnounceRelay m_relay = new();
	private readonly SelectableTextBlock m_playerText = new() { TextWrapping = TextWrapping.Wrap, FontFamily = Ui.Mono };
	private readonly CheckBox m_cbRelay = new() { Content = "Показывать плеер в Rider и Unity Editor автоматически (повторять анонс плеера в сети ПК)" };
	private readonly CheckBox m_cbRecordProfiler = new() { Content = "Записывать профиль в файл на Deck при каждом запуске игры" };
	private readonly TextBox m_tbProfilerFrames = new() { Width = 140, PlaceholderText = "до закрытия игры" };
	private volatile UnityPlayerInfo? m_playerInfo;

	private Control BuildDebugTab()
	{
		m_cbRelay.IsCheckedChanged += (_, _) => UpdateDebugView();
		m_cbRecordProfiler.IsCheckedChanged += (_, _) => UpdateProfile(p => p.RecordProfiler = m_cbRecordProfiler.IsChecked == true);
		BindText(m_tbProfilerFrames, (p, v) => p.ProfilerFrameCount = int.TryParse(v.Trim(), out int frames) && frames > 0 ? frames : 0);

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
			m_cbRecordProfiler,
			Ui.Row(new TextBlock { Text = "Записать кадров:", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, m_tbProfilerFrames),
			Ui.Row(
				Ui.Button("Скачать последнюю запись…", DownloadProfilerRecordingAsync),
				Ui.Button("Удалить записи на Deck", DeleteProfilerRecordingsAsync)),
			Ui.Hint("Запись в файл: игра стартует с -profiler-enable -profiler-log-file и пишет .raw на Deck с первого кадра, " +
			        "включая загрузку, а сеть на замеры не влияет. Работает в Development-сборке и при запуске кнопками программы " +
			        "(и из Unity). Без лимита кадров файл растёт, пока игра запущена, — закройте её перед скачиванием. " +
			        "Открыть запись: Unity → Profiler → Load.")));

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
			Content = Ui.Column(10, playerGroup, profilerGroup, prepareGroup, riderGroup)
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

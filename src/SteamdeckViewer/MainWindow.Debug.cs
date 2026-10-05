using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SteamdeckViewer.Core;

namespace SteamdeckViewer;

// Вкладка «Отладка (Rider)»: адрес и порт отладчика Unity-плеера на Deck, ретрансляция анонса для Rider
internal sealed partial class MainWindow
{
	private readonly UnityAnnounceRelay m_relay = new();
	private readonly SelectableTextBlock m_playerText = new() { TextWrapping = TextWrapping.Wrap, FontFamily = Ui.Mono };
	private readonly CheckBox m_cbRelay = new() { Content = "Показывать плеер в Rider автоматически (повторять анонс плеера в сети ПК)" };
	private volatile UnityPlayerInfo? m_playerInfo;

	private Control BuildDebugTab()
	{
		m_cbRelay.IsCheckedChanged += (_, _) => UpdateDebugView();

		var playerGroup = Ui.Group("Unity-плеер на Deck", Ui.Column(10,
			m_playerText,
			Ui.Row(
				Ui.Button("Скопировать адрес:порт", CopyDebuggerAddressAsync),
				Ui.Button("Найти в Player.log", FindPlayerInLogAsync),
				Ui.Button("Следить за логом", StartLogTail)),
			m_cbRelay,
			Ui.Hint("Rider ищет плееры по UDP-анонсам на 225.0.0.222:54997. По Wi-Fi multicast от Deck часто не доходит; " +
			        "с этой галочкой приложение раз в секунду повторяет анонс плеера с адресом Deck (экспериментально). " +
			        "Сведения о плеере берутся из Player.log, поэтому слежение за логом должно быть включено.")));

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
				"Брандмауэр Windows должен пропускать входящий UDP для Rider, а порт 56000–56999 на Deck — быть доступен по сети.\n\n" +
				"Unity Profiler: Window → Analysis → Profiler → список целей → <Enter IP> → IP Deck (нужен Development Build)."
		});

		UpdateDebugView();

		return new ScrollViewer
		{
			Margin = new Thickness(0, 10, 0, 10),
			Content = Ui.Column(10, playerGroup, prepareGroup, riderGroup)
		};
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

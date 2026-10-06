using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SteamdeckViewer.Core;

namespace SteamdeckViewer;

// Папки обмена с рабочим столом Deck: пока есть подключение, новые и изменённые файлы раз в 2 секунды передаются
// в обе стороны. В стриме файл перетаскивается в «На ПК» на рабочем столе Deck и появляется в папке на ПК
internal sealed partial class MainWindow
{
	private static readonly TimeSpan ExchangeInterval = TimeSpan.FromSeconds(2);

	private readonly CheckBox m_cbExchange = new() { Content = "Включены" };
	private readonly SelectableTextBlock m_exchangeText = new() { TextWrapping = TextWrapping.Wrap };
	private readonly TextBlock m_exchangeLast = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
	private CancellationTokenSource? m_exchangeLoop;
	private ExchangeFolders? m_exchange;

	private string ExchangeRoot => string.IsNullOrWhiteSpace(m_settings.ExchangeFolder) ? ExchangeFolders.DefaultPcRoot() : m_settings.ExchangeFolder;

	private Control BuildExchangeGroup()
	{
		m_cbExchange.IsChecked = m_settings.ExchangeEnabled;
		m_cbExchange.IsCheckedChanged += (_, _) =>
		{
			m_settings.ExchangeEnabled = m_cbExchange.IsChecked == true;
			m_settings.Save();
			RestartExchange();
		};

		ShowExchangeState();
		return Ui.Group("Папки обмена", Ui.Column(6,
			Ui.Row(
				m_cbExchange,
				Ui.Button("Открыть на ПК", OpenExchangeOnPcAsync),
				Ui.Button("Открыть на Deck", OpenExchangeOnDeckAsync),
				Ui.Button("Папка на ПК…", ChangeExchangeFolderAsync)),
			m_exchangeText,
			m_exchangeLast));
	}

	private void RestartExchange()
	{
		StopExchange();
		if (m_settings.ExchangeEnabled && m_deck is { } deck)
		{
			var exchange = new ExchangeFolders(ExchangeRoot, ExchangeFolders.DefaultManifestPath(deck.Device));
			var loop = CancellationTokenSource.CreateLinkedTokenSource(m_lifetime.Token);
			m_exchange = exchange;
			m_exchangeLoop = loop;
			_ = Task.Run(() => RunExchangeAsync(deck, exchange, loop));
		}

		ShowExchangeState();
	}

	private void StopExchange()
	{
		CancellationTokenSource? loop = m_exchangeLoop;
		m_exchangeLoop = null;
		m_exchange = null;
		try
		{
			loop?.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// цикл уже завершился сам (оборвалась связь с Deck) и освободил источник отмены
		}
	}

	// Цикл владеет своим CancellationTokenSource и освобождает его сам: StopExchange только отменяет
	private async Task RunExchangeAsync(DeckConnection deck, ExchangeFolders exchange, CancellationTokenSource loop)
	{
		using CancellationTokenSource owned = loop;
		CancellationToken ct = owned.Token;
		string? lastError = null;
		bool prepared = false;

		while (!ct.IsCancellationRequested && deck.IsConnected)
		{
			string? error = null;
			try
			{
				ExchangeResult result = await exchange.RunOnceAsync(deck, ct);
				if (!prepared)
				{
					prepared = true;
					Dispatcher.UIThread.Post(ShowExchangeState);
				}

				if (!result.IsEmpty)
				{
					Dispatcher.UIThread.Post(() => ShowExchangeResult(result));
				}
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				return;
			}
			catch (Exception e)
			{
				error = e.Message;
			}

			// Одну и ту же ошибку показываем один раз, а не каждые 2 секунды
			if (error != lastError)
			{
				lastError = error;
				Dispatcher.UIThread.Post(() => m_exchangeLast.Text = error != null ? "Ошибка обмена: " + error : string.Empty);
			}

			try
			{
				await Task.Delay(ExchangeInterval, ct);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
	}

	private void ShowExchangeState()
	{
		string desktop = m_exchange?.DeckDesktop is { } path ? $"рабочем столе Deck ({path})" : "рабочем столе Deck";
		string inbox = Path.Combine(ExchangeRoot, ExchangeFolders.PcInbox);
		string outbox = Path.Combine(ExchangeRoot, ExchangeFolders.PcOutbox);

		m_exchangeText.Text = !m_settings.ExchangeEnabled
			? "Выключены."
			: $"Deck → ПК: папка «{ExchangeFolders.DeckOutbox}» на {desktop} → {inbox}\n" +
			  $"ПК → Deck: {outbox} → папка «{ExchangeFolders.DeckInbox}» на {desktop}\n" +
			  "В стриме перетащите файл в «На ПК» — через пару секунд он появится на ПК. Удаление не передаётся, изменённый файл передаётся заново." +
			  (m_deck == null ? "\nЗаработают после подключения к Deck." : string.Empty);
	}

	private void ShowExchangeResult(ExchangeResult result)
	{
		var parts = new List<string>();
		if (result.ToPc.Count > 0)
		{
			parts.Add("на ПК: " + Names(result.ToPc));
		}

		if (result.ToDeck.Count > 0)
		{
			parts.Add("на Deck: " + Names(result.ToDeck));
		}

		if (result.Errors.Count > 0)
		{
			parts.Add("не удалось (повторю): " + string.Join("; ", result.Errors));
		}

		string text = $"{DateTime.Now:HH:mm:ss} — " + string.Join(", ", parts);
		m_exchangeLast.Text = text;
		if (result.ToPc.Count > 0 || result.ToDeck.Count > 0)
		{
			SetStatus("Папки обмена: " + string.Join(", ", parts));
		}

		static string Names(IReadOnlyList<string> files)
		{
			return files.Count <= 3 ? string.Join(", ", files) : string.Join(", ", files.Take(3)) + $" и ещё {files.Count - 3}";
		}
	}

	private async Task OpenExchangeOnPcAsync()
	{
		Directory.CreateDirectory(Path.Combine(ExchangeRoot, ExchangeFolders.PcInbox));
		Directory.CreateDirectory(Path.Combine(ExchangeRoot, ExchangeFolders.PcOutbox));
		await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(ExchangeRoot));
	}

	private async Task OpenExchangeOnDeckAsync()
	{
		if (m_deck == null)
		{
			await Dialogs.Info(this, "Нет подключения к Deck.");
			return;
		}

		await NavigateFilesAsync(m_exchange?.DeckDesktop ?? "~/Desktop");
	}

	private async Task ChangeExchangeFolderAsync()
	{
		IStorageFolder? start = await StorageProvider.TryGetFolderFromPathAsync(Directory.Exists(ExchangeRoot) ? ExchangeRoot : ExchangeFolders.DefaultPcRoot());
		IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
		{
			AllowMultiple = false,
			Title = $"Папка обмена на ПК (внутри появятся «{ExchangeFolders.PcInbox}» и «{ExchangeFolders.PcOutbox}»)",
			SuggestedStartLocation = start
		});

		string? path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
		if (path == null)
		{
			return;
		}

		m_settings.ExchangeFolder = path;
		m_settings.Save();
		RestartExchange();
	}
}

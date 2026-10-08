using Avalonia.Threading;
using SteamdeckViewer.Core;

namespace SteamdeckViewer;

// Новая версия программы: автор публикует номер и ссылку на exe в документе AppInfo.UpdateUrl (AppUpdate),
// программа проверяет его при запуске и раз в несколько часов и предлагает открыть ссылку
internal sealed partial class MainWindow
{
	private readonly DispatcherTimer m_updateTimer = new() { Interval = TimeSpan.FromHours(3) };

	// О какой версии уже напомнили в этом сеансе: после «Позже» таймер не повторяет то же окно
	private Version? m_updateOffered;

	private void StartUpdateChecks()
	{
		if (string.IsNullOrWhiteSpace(AppInfo.UpdateUrl))
		{
			return;
		}

		m_updateTimer.Tick += async (_, _) => await CheckForUpdateAsync(manual: false);
		m_updateTimer.Start();
		_ = CheckForUpdateAsync(manual: false);
	}

	private async Task CheckForUpdateAsync(bool manual)
	{
		if (string.IsNullOrWhiteSpace(AppInfo.UpdateUrl))
		{
			if (manual)
			{
				await Dialogs.Info(this, "Адрес объявлений о новых версиях в этой сборке не задан (AppInfo.UpdateUrl).");
			}

			return;
		}

		UpdateNotice? notice;
		try
		{
			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
			notice = await AppUpdate.FetchAsync(http, AppInfo.UpdateUrl, m_lifetime.Token);
		}
		catch (Exception e) when (!m_lifetime.IsCancellationRequested)
		{
			if (manual)
			{
				await Dialogs.Error(this, "Не удалось проверить обновления: " + e.Message);
			}

			return;
		}

		if (notice == null || !AppUpdate.IsNewer(notice, AppInfo.AppVersionText))
		{
			if (manual)
			{
				await Dialogs.Info(this, notice == null
					? "В документе с объявлением о новой версии нет строк «version:» и «url:» — или он закрыт для доступа по ссылке."
					: $"У вас последняя версия: {AppInfo.AppVersionText}.");
			}

			return;
		}

		if (!manual && notice.Version == m_updateOffered)
		{
			return;
		}

		m_updateOffered = notice.Version;
		string version = notice.Version.ToString(notice.Version.Revision > 0 ? 4 : 3);
		string text = $"Нужно обновиться: вышла версия {version}, у вас {AppInfo.AppVersionText}." +
		              (notice.Message.Length > 0 ? "\n\n" + notice.Message : string.Empty) +
		              $"\n\nOK откроет ссылку на новую версию. Скачайте CarXDeckTools.exe и замените им этот:\n{Environment.ProcessPath}";
		if (await Dialogs.Ask(this, text, "Обновление " + AppInfo.Name, "OK", "Позже"))
		{
			await Launcher.LaunchUriAsync(new Uri(notice.Url));
		}
	}
}

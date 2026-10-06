using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SteamdeckViewer.Core;

namespace SteamdeckViewer;

// Вкладка «Билды»: профиль тестового билда, заливка с дельтой, ярлык Devkit Game, запуск/остановка и живой Player.log
internal sealed partial class MainWindow
{
	private static readonly string[] RuntimeNames =
	[
		"Нативный Linux (без рантайма)",
		"Steam Linux Runtime 3.0 (sniper)",
		"Proton (Windows-билд)"
	];

	private static readonly string[] LaunchModeNames =
	[
		"Через Steam — ярлык «Devkit Game» (для Game Mode)",
		"Напрямую — systemd-run (удобно в рабочем столе)"
	];

	private readonly ComboBox m_cbProfiles = new() { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
	private readonly TextBox m_tbProfileName = new();
	private readonly TextBlock m_gameIdHint = new() { Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
	private readonly TextBox m_tbBuildFolder = new() { PlaceholderText = @"C:\Builds\CarX_Street_Linux" };
	private readonly TextBox m_tbExecutable = new();
	private readonly TextBox m_tbArguments = new() { PlaceholderText = "-screen-fullscreen 1 -logFile …" };
	private readonly TextBox m_tbEnvironment = new() { PlaceholderText = "KEY=VALUE KEY2=VALUE2" };
	private readonly ComboBox m_cbRuntime = new() { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = RuntimeNames };
	private readonly ComboBox m_cbLaunchMode = new() { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = LaunchModeNames };
	private readonly TextBox m_tbExcludes = new();
	private readonly TextBox m_tbAppId = new() { PlaceholderText = "пусто — steam_appid.txt не создаётся" };
	private readonly TextBox m_tbPlayerLog = new();
	private readonly CheckBox m_cbDeleteExtraneous = new() { Content = "Удалять на Deck файлы, которых нет в билде (нужно для проверки целостности CarX Street)" };
	private readonly CheckBox m_cbStopBeforeUpload = new() { Content = "Останавливать игру перед заливкой" };

	private readonly ProgressBar m_buildProgress = new() { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
	private readonly TextBlock m_buildProgressText = new() { Opacity = 0.75, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
	private readonly Button m_btnCancelDeploy = new() { Content = "Отмена", IsEnabled = false };
	private readonly List<Button> m_buildButtons = [];

	private readonly LogView m_playerLog = new();
	private readonly Button m_btnTail = new() { MinWidth = 170 };
	private readonly DispatcherTimer m_saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

	private CancellationTokenSource? m_deployCts;
	private CancellationTokenSource? m_tailCts;
	private bool m_loadingProfile;

	private BuildProfile CurrentProfile => m_cbProfiles.SelectedItem as BuildProfile ?? m_settings.Profiles[0];

	private Control BuildBuildsTab()
	{
		m_saveTimer.Tick += (_, _) =>
		{
			m_saveTimer.Stop();
			m_settings.Save();
		};

		var profileRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
		profileRow.Children.Add(m_cbProfiles);
		var profileButtons = Ui.Row(Ui.Button("Новый", AddProfile), Ui.Button("Копия", CloneProfile), Ui.Button("Удалить", RemoveProfileAsync));
		profileButtons.Margin = new Thickness(8, 0, 0, 0);
		Grid.SetColumn(profileButtons, 1);
		profileRow.Children.Add(profileButtons);

		var form = new FormGrid();
		form.Add("Название", m_tbProfileName);
		form.AddFull(m_gameIdHint);
		form.Add("Папка билда на ПК", m_tbBuildFolder, Ui.Button("Обзор…", BrowseBuildFolderAsync));
		form.Add("Исполняемый файл", m_tbExecutable, Ui.Button("Выбрать…", BrowseExecutableAsync));
		form.Add("Аргументы запуска", m_tbArguments);
		form.Add("Переменные окружения", m_tbEnvironment);
		form.Add("Среда выполнения", m_cbRuntime);
		form.Add("Способ запуска", m_cbLaunchMode);
		form.Add("Не заливать", m_tbExcludes);
		form.Add("Steam AppID", m_tbAppId);
		form.Add("Player.log на Deck", m_tbPlayerLog);
		form.AddFull(m_cbDeleteExtraneous);
		form.AddFull(m_cbStopBeforeUpload);

		var actions = new WrapPanel { Orientation = Orientation.Horizontal };
		foreach (Button button in new[]
		         {
			         Ui.Button("Залить", () => DeployAsync(launchAfter: false)),
			         Ui.Button("Залить и запустить", () => DeployAsync(launchAfter: true)),
			         Ui.Button("Запустить", LaunchGameAsync),
			         Ui.Button("Остановить", StopGameAsync),
			         Ui.Button("Удалить с Deck", DeleteGameAsync)
		         })
		{
			button.Margin = new Thickness(0, 0, 8, 8);
			m_buildButtons.Add(button);
			actions.Children.Add(button);
		}

		m_btnCancelDeploy.Margin = new Thickness(0, 0, 8, 8);
		m_btnCancelDeploy.Click += (_, _) => m_deployCts?.Cancel();
		actions.Children.Add(m_btnCancelDeploy);

		var left = Ui.Column(0,
			profileRow,
			Ui.Group("Профиль билда", form.Grid),
			new Border { Height = 10 },
			Ui.Group("Действия", Ui.Column(6, actions, m_buildProgress, m_buildProgressText)));

		// Лог игрока
		m_btnTail.Click += (_, _) =>
		{
			if (m_tailCts != null)
			{
				StopLogTail();
			}
			else
			{
				StartLogTail();
			}
		};
		UpdateTailButton();

		var logToolbar = Ui.Row(m_btnTail,
			Ui.Button("Очистить", () => m_playerLog.Clear()),
			Ui.Button("Скачать Player.log…", DownloadPlayerLogAsync));
		logToolbar.Margin = new Thickness(0, 0, 0, 8);

		var logPanel = new DockPanel();
		DockPanel.SetDock(logToolbar, Dock.Top);
		logPanel.Children.Add(logToolbar);
		logPanel.Children.Add(m_playerLog.Box);

		var root = new Grid { ColumnDefinitions = new ColumnDefinitions("700,6,*"), Margin = new Thickness(0, 10, 0, 10) };
		root.ColumnDefinitions[0].MinWidth = 480;
		root.ColumnDefinitions[2].MinWidth = 320;
		root.Children.Add(new ScrollViewer { Content = left });

		var splitter = new GridSplitter { Background = Ui.SplitterBrush, ResizeDirection = GridResizeDirection.Columns };
		Grid.SetColumn(splitter, 1);
		root.Children.Add(splitter);

		Border logGroup = Ui.Group("Player.log", logPanel);
		Grid.SetColumn(logGroup, 2);
		root.Children.Add(logGroup);

		SetupProfileBinding();
		ReloadProfiles();
		return root;
	}

	// ---------------------------------------------------------------- профили

	private void SetupProfileBinding()
	{
		m_cbProfiles.SelectionChanged += (_, _) =>
		{
			if (m_cbProfiles.SelectedItem is BuildProfile profile)
			{
				m_settings.SelectedProfile = m_settings.Profiles.IndexOf(profile);
				ScheduleSave();
				LoadProfileIntoForm(profile);
			}
		};

		BindText(m_tbProfileName, (p, v) => p.Name = v);
		BindText(m_tbBuildFolder, (p, v) => p.LocalFolder = v);
		BindText(m_tbExecutable, (p, v) => p.Executable = v);
		BindText(m_tbArguments, (p, v) => p.Arguments = v);
		BindText(m_tbEnvironment, (p, v) => p.EnvironmentVariables = v);
		BindText(m_tbExcludes, (p, v) => p.Excludes = v);
		BindText(m_tbAppId, (p, v) => p.SteamAppId = v);
		BindText(m_tbPlayerLog, (p, v) => p.PlayerLogPath = v);

		m_tbProfileName.LostFocus += (_, _) => ReloadProfiles();

		m_cbRuntime.SelectionChanged += (_, _) => UpdateProfile(p => p.Runtime = (DeckRuntime)Math.Max(0, m_cbRuntime.SelectedIndex));
		m_cbLaunchMode.SelectionChanged += (_, _) => UpdateProfile(p => p.LaunchMode = (LaunchMode)Math.Max(0, m_cbLaunchMode.SelectedIndex));
		m_cbDeleteExtraneous.IsCheckedChanged += (_, _) => UpdateProfile(p => p.DeleteExtraneous = m_cbDeleteExtraneous.IsChecked == true);
		m_cbStopBeforeUpload.IsCheckedChanged += (_, _) => UpdateProfile(p => p.StopBeforeUpload = m_cbStopBeforeUpload.IsChecked == true);
	}

	private void BindText(TextBox box, Action<BuildProfile, string> apply)
	{
		box.TextChanged += (_, _) => UpdateProfile(p => apply(p, box.Text ?? string.Empty));
	}

	private void UpdateProfile(Action<BuildProfile> apply)
	{
		if (m_loadingProfile || m_cbProfiles.SelectedItem is not BuildProfile profile)
		{
			return;
		}

		apply(profile);
		UpdateGameIdHint(profile);
		ScheduleSave();
	}

	private void ScheduleSave()
	{
		m_saveTimer.Stop();
		m_saveTimer.Start();
	}

	private void ReloadProfiles()
	{
		BuildProfile? selected = m_cbProfiles.SelectedItem as BuildProfile;
		m_loadingProfile = true;
		m_cbProfiles.ItemsSource = null;
		m_cbProfiles.ItemsSource = m_settings.Profiles.ToList();
		m_loadingProfile = false;

		int index = selected != null ? m_settings.Profiles.IndexOf(selected) : m_settings.SelectedProfile;
		m_cbProfiles.SelectedIndex = Math.Clamp(index, 0, m_settings.Profiles.Count - 1);
	}

	private void LoadProfileIntoForm(BuildProfile profile)
	{
		m_loadingProfile = true;
		m_tbProfileName.Text = profile.Name;
		m_tbBuildFolder.Text = profile.LocalFolder;
		m_tbExecutable.Text = profile.Executable;
		m_tbArguments.Text = profile.Arguments;
		m_tbEnvironment.Text = profile.EnvironmentVariables;
		m_cbRuntime.SelectedIndex = (int)profile.Runtime;
		m_cbLaunchMode.SelectedIndex = (int)profile.LaunchMode;
		m_tbExcludes.Text = profile.Excludes;
		m_tbAppId.Text = profile.SteamAppId;
		m_tbPlayerLog.Text = profile.PlayerLogPath;
		m_cbDeleteExtraneous.IsChecked = profile.DeleteExtraneous;
		m_cbStopBeforeUpload.IsChecked = profile.StopBeforeUpload;
		m_loadingProfile = false;
		UpdateGameIdHint(profile);
	}

	private void UpdateGameIdHint(BuildProfile profile)
	{
		m_gameIdHint.Text = $"На Deck: {profile.RemoteFolder} · в библиотеке Steam: «Devkit Game: {profile.GameId}»";
	}

	private void AddProfile()
	{
		var profile = new BuildProfile { Name = "Новый билд " + (m_settings.Profiles.Count + 1) };
		m_settings.Profiles.Add(profile);
		m_settings.SelectedProfile = m_settings.Profiles.Count - 1;
		m_settings.Save();
		m_cbProfiles.SelectedItem = null;
		ReloadProfiles();
		m_cbProfiles.SelectedItem = profile;
	}

	private void CloneProfile()
	{
		BuildProfile source = CurrentProfile;
		var json = System.Text.Json.JsonSerializer.Serialize(source);
		BuildProfile copy = System.Text.Json.JsonSerializer.Deserialize<BuildProfile>(json)!;
		copy.Name = source.Name + " (копия)";
		m_settings.Profiles.Add(copy);
		m_settings.Save();
		ReloadProfiles();
		m_cbProfiles.SelectedItem = copy;
	}

	private async Task RemoveProfileAsync()
	{
		if (m_settings.Profiles.Count <= 1)
		{
			await Dialogs.Info(this, "Должен остаться хотя бы один профиль.");
			return;
		}

		BuildProfile profile = CurrentProfile;
		if (!await Dialogs.YesNo(this, $"Удалить профиль «{profile.Name}»? Залитый на Deck билд останется.", "Удаление профиля"))
		{
			return;
		}

		m_settings.Profiles.Remove(profile);
		m_settings.SelectedProfile = 0;
		m_settings.Save();
		m_cbProfiles.SelectedItem = null;
		ReloadProfiles();
	}

	private async Task BrowseBuildFolderAsync()
	{
		IStorageFolder? start = Directory.Exists(m_tbBuildFolder.Text) ? await StorageProvider.TryGetFolderFromPathAsync(m_tbBuildFolder.Text!) : null;
		IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
		{
			AllowMultiple = false,
			Title = "Папка Linux-билда",
			SuggestedStartLocation = start
		});
		string? folder = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
		if (folder == null)
		{
			return;
		}

		m_tbBuildFolder.Text = folder;

		// Unity-билд под Linux: ровно один *.x86_64 рядом с папкой *_Data — подставляем его сами
		if (!File.Exists(Path.Combine(folder, m_tbExecutable.Text ?? string.Empty)))
		{
			string[] candidates = Directory.GetFiles(folder, "*.x86_64", SearchOption.TopDirectoryOnly);
			if (candidates.Length == 1)
			{
				m_tbExecutable.Text = Path.GetFileName(candidates[0]);
			}
		}
	}

	private async Task BrowseExecutableAsync()
	{
		string folder = m_tbBuildFolder.Text ?? string.Empty;
		IStorageFolder? start = Directory.Exists(folder) ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null;
		IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			AllowMultiple = false,
			Title = "Исполняемый файл билда",
			SuggestedStartLocation = start
		});
		string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
		if (path == null)
		{
			return;
		}

		string relative = Directory.Exists(folder) ? Path.GetRelativePath(folder, path) : path;
		if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
		{
			await Dialogs.Error(this, "Исполняемый файл должен лежать внутри папки билда.");
			return;
		}

		m_tbExecutable.Text = relative.Replace('\\', '/');
	}

	// ---------------------------------------------------------------- заливка и запуск

	private async Task DeployAsync(bool launchAfter)
	{
		BuildProfile profile = CurrentProfile;
		if (!Directory.Exists(profile.LocalFolder))
		{
			await Dialogs.Error(this, "Папка билда на ПК не найдена. Укажите её в профиле.");
			return;
		}

		if (!File.Exists(Path.Combine(profile.LocalFolder, profile.Executable)) &&
		    !await Dialogs.YesNo(this, $"В папке билда нет «{profile.Executable}». Всё равно залить?", "Исполняемый файл не найден"))
		{
			return;
		}

		using var cts = CancellationTokenSource.CreateLinkedTokenSource(m_lifetime.Token);
		m_deployCts = cts;
		SetDeployUi(true);

		SyncSummary? summary = null;
		string? shortcutProblem = null;
		var progress = new Progress<SyncProgress>(ShowBuildProgress);
		bool ok = await RunOnDeckAsync("Заливка билда…", async (deck, _) =>
		{
			if (profile.StopBeforeUpload)
			{
				await DevkitGames.StopAsync(deck, profile, cts.Token);
			}

			await DevkitGames.MigrateLegacyFolderAsync(deck, profile, cts.Token);
			summary = await FolderSync.SyncAsync(deck, profile.LocalFolder, profile.RemoteFolder,
				new FileFilter(profile.Excludes), profile.DeleteExtraneous, progress, cts.Token);

			await DevkitGames.WriteSteamAppIdAsync(deck, profile, cts.Token);
			if (profile.LaunchMode == LaunchMode.Steam)
			{
				// Файлы уже на Deck: без ярлыка билд всё равно можно запустить напрямую, поэтому это не провал заливки
				try
				{
					await DevkitGames.RegisterShortcutAsync(deck, profile, cts.Token);
				}
				catch (InvalidOperationException e)
				{
					shortcutProblem = e.Message;
					return;
				}
			}

			if (launchAfter)
			{
				await DevkitGames.LaunchAsync(deck, profile, cts.Token);
			}
		}, cts.Token);

		if (shortcutProblem != null)
		{
			m_playerLog.Append("[CarX Deck Tools] ярлык Steam не создан: " + shortcutProblem);
			await Dialogs.Info(this, $"Билд залит, но ярлык «Devkit Game: {profile.GameId}» в Steam не создан:\n{shortcutProblem}\n\n" +
			                         "Запустите Steam на Deck и повторите заливку (изменённых файлов не будет — уйдёт только регистрация) " +
			                         "или выберите способ запуска «Напрямую».", "Заливка");
			ok = false;
		}

		m_deployCts = null;
		SetDeployUi(false);

		if (summary != null)
		{
			double seconds = Math.Max(summary.Elapsed.TotalSeconds, 0.001);
			string text = $"Залито файлов: {summary.Uploaded} ({DeckStatus.FormatBytes(summary.Bytes)}), без изменений: {summary.Unchanged}, " +
			              $"удалено: {summary.Deleted} · {summary.Elapsed:mm\\:ss} · {DeckStatus.FormatBytes((long)(summary.Bytes / seconds))}/с";
			m_buildProgressText.Text = text;
			m_playerLog.Append("[CarX Deck Tools] " + text);
		}

		if (ok)
		{
			SetStatus(launchAfter ? "Билд залит и запущен" : "Билд залит");
			if (launchAfter)
			{
				StartLogTail();
			}
		}
	}

	private async Task LaunchGameAsync()
	{
		BuildProfile profile = CurrentProfile;
		if (await RunOnDeckAsync("Запуск…", (deck, ct) => DevkitGames.LaunchAsync(deck, profile, ct)))
		{
			SetStatus($"Запущено: {profile.Name}");
			StartLogTail();
		}
	}

	private async Task StopGameAsync()
	{
		BuildProfile profile = CurrentProfile;
		int stopped = 0;
		if (await RunOnDeckAsync("Остановка…", async (deck, ct) => stopped = await DevkitGames.StopAsync(deck, profile, ct)))
		{
			SetStatus(stopped > 0 ? $"Остановлено процессов: {stopped}" : "Игра не запущена");
		}
	}

	private async Task DeleteGameAsync()
	{
		BuildProfile profile = CurrentProfile;
		if (!await Dialogs.YesNo(this, $"Удалить с Deck папку {profile.RemoteFolder} и ярлык «Devkit Game: {profile.GameId}»?", "Удаление билда"))
		{
			return;
		}

		if (await RunOnDeckAsync("Удаление билда…", (deck, ct) => DevkitGames.DeleteAsync(deck, profile, ct)))
		{
			SetStatus("Билд удалён с Deck");
		}
	}

	private void SetDeployUi(bool deploying)
	{
		foreach (Button button in m_buildButtons)
		{
			button.IsEnabled = !deploying;
		}

		m_btnCancelDeploy.IsEnabled = deploying;
		m_buildProgress.IsVisible = deploying;
		m_buildProgress.Value = 0;
	}

	private void ShowBuildProgress(SyncProgress p)
	{
		m_buildProgress.Value = p.TotalBytes > 0 ? (double)p.DoneBytes / p.TotalBytes : 0;
		m_buildProgressText.Text = p.TotalFiles > 0
			? $"{p.Stage}: {p.DoneFiles}/{p.TotalFiles} файлов · {DeckStatus.FormatBytes(p.DoneBytes)} из {DeckStatus.FormatBytes(p.TotalBytes)}"
			: p.Stage + "…";
	}

	// ---------------------------------------------------------------- Player.log

	private void StartLogTail()
	{
		DeckConnection? deck = m_deck;
		if (deck == null || m_tailCts != null)
		{
			return;
		}

		var cts = CancellationTokenSource.CreateLinkedTokenSource(m_lifetime.Token);
		m_tailCts = cts;
		m_playerInfo = null;
		UpdateTailButton();
		UpdateDebugView();

		string path = CurrentProfile.PlayerLogPath;
		m_playerLog.Append($"[CarX Deck Tools] слежение за {path}");

		_ = Task.Run(async () =>
		{
			try
			{
				// -F переживает пересоздание файла: Unity при каждом запуске начинает Player.log заново
				await deck.StreamLinesAsync($"tail -n 300 -F {Sh.Path(path)} 2>&1", OnPlayerLogLine, cts.Token);
			}
			catch (Exception e)
			{
				m_playerLog.Append("[CarX Deck Tools] слежение остановлено: " + e.Message);
			}
			finally
			{
				Dispatcher.UIThread.Post(() =>
				{
					if (m_tailCts == cts)
					{
						m_tailCts = null;
						UpdateTailButton();
					}
					cts.Dispose();
				});
			}
		});
	}

	private void StopLogTail()
	{
		m_tailCts?.Cancel();
		m_tailCts = null;
		UpdateTailButton();
	}

	private void UpdateTailButton()
	{
		m_btnTail.Content = m_tailCts != null ? "■ Остановить слежение" : "▶ Следить за логом";
	}

	// Вызывается из фонового потока чтения SSH
	private void OnPlayerLogLine(string line)
	{
		m_playerLog.Append(line);

		if (line.Contains("has been replaced", StringComparison.Ordinal) || line.Contains("file truncated", StringComparison.Ordinal))
		{
			m_playerInfo = null;
			Dispatcher.UIThread.Post(UpdateDebugView);
			return;
		}

		UnityPlayerInfo? info = UnityPlayerLog.Parse(line, m_playerInfo);
		if (info != null)
		{
			m_playerInfo = info;
			Dispatcher.UIThread.Post(UpdateDebugView);
		}
	}

	private async Task DownloadPlayerLogAsync()
	{
		DeckConnection? deck = m_deck;
		if (deck == null)
		{
			await Dialogs.Info(this, "Нет подключения к Deck.");
			return;
		}

		IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = "Сохранить Player.log",
			SuggestedFileName = $"Player_{DateTime.Now:yyyyMMdd_HHmmss}.log",
			DefaultExtension = "log"
		});
		string? localPath = file?.TryGetLocalPath();
		if (localPath == null)
		{
			return;
		}

		string remotePath = deck.ResolvePath(CurrentProfile.PlayerLogPath);
		if (await RunOnDeckAsync("Скачивание Player.log…", (d, _) =>
		    {
			    using FileStream output = File.Create(localPath);
			    d.GetSftp().DownloadFile(remotePath, output);
			    return Task.CompletedTask;
		    }))
		{
			SetStatus("Player.log сохранён: " + localPath);
		}
	}
}

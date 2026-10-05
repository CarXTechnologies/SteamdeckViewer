using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using SteamdeckViewer.Core;

namespace SteamdeckViewer;

internal sealed record RemoteEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTime Modified);

// Вкладка «Файлы»: обзор Deck по SFTP, заливка tar-потоком (кнопки или перетаскивание из Проводника), скачивание
internal sealed partial class MainWindow
{
	private readonly TextBox m_tbRemotePath = new() { VerticalAlignment = VerticalAlignment.Center };
	private readonly ListBox m_lbFiles = new() { SelectionMode = SelectionMode.Multiple };
	private readonly ProgressBar m_filesProgress = new() { Minimum = 0, Maximum = 1, IsVisible = false, Height = 6 };
	private readonly TextBlock m_filesProgressText = new() { Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
	private string? m_currentRemotePath;

	private Control BuildFilesTab()
	{
		m_tbRemotePath.KeyDown += async (_, e) =>
		{
			if (e.Key == Key.Enter)
			{
				await NavigateFilesAsync(m_tbRemotePath.Text?.Trim() ?? "~");
			}
		};

		m_lbFiles.ItemTemplate = new FuncDataTemplate<RemoteEntry>((entry, _) => entry == null ? new TextBlock() : BuildFileRow(entry));
		m_lbFiles.DoubleTapped += async (_, _) =>
		{
			if (m_lbFiles.SelectedItem is RemoteEntry { IsDirectory: true } entry)
			{
				await NavigateFilesAsync(entry.FullPath);
			}
		};

		DragDrop.SetAllowDrop(m_lbFiles, true);
		m_lbFiles.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = m_deck != null ? DragDropEffects.Copy : DragDropEffects.None);
		m_lbFiles.AddHandler(DragDrop.DropEvent, async (_, e) =>
		{
			IStorageItem[]? items = e.DataTransfer.TryGetFiles();
			if (items is { Length: > 0 })
			{
				await UploadLocalItemsAsync(items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList());
			}
		});

		var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
		AddToGrid(header, Ui.Row(
			Ui.Button("↑ Вверх", () => NavigateFilesAsync(Sh.ParentPath(m_currentRemotePath ?? "~"))),
			Ui.Button("Домой", () => NavigateFilesAsync("~")),
			Ui.Button("Билды", () => NavigateFilesAsync(DevkitGames.GamesRoot))), 0);
		m_tbRemotePath.Margin = new Thickness(8, 0, 8, 0);
		AddToGrid(header, m_tbRemotePath, 1);
		AddToGrid(header, Ui.Button("Обновить", () => NavigateFilesAsync(m_currentRemotePath ?? "~")), 2);

		var columns = new Grid { ColumnDefinitions = FileColumns(), Margin = new Thickness(12, 0, 12, 4), Opacity = 0.7 };
		AddToGrid(columns, new TextBlock { Text = "Имя" }, 0);
		AddToGrid(columns, new TextBlock { Text = "Размер", HorizontalAlignment = HorizontalAlignment.Right }, 1);
		AddToGrid(columns, new TextBlock { Text = "Изменён", Margin = new Thickness(16, 0, 0, 0) }, 2);

		var buttons = Ui.Row(
			Ui.Button("Загрузить файлы…", UploadFilesAsync),
			Ui.Button("Загрузить папку…", UploadFolderAsync),
			Ui.Button("Скачать…", DownloadSelectedAsync),
			Ui.Button("Новая папка…", CreateFolderAsync),
			Ui.Button("Переименовать…", RenameSelectedAsync),
			Ui.Button("Удалить", DeleteSelectedAsync),
			m_filesProgressText);

		var footer = Ui.Column(6, m_filesProgress, buttons, Ui.Hint("Файлы и папки можно перетащить из Проводника прямо в список — они загрузятся в текущую папку Deck."));
		footer.Margin = new Thickness(0, 8, 0, 0);

		var root = new DockPanel { Margin = new Thickness(0, 10, 0, 10) };
		DockPanel.SetDock(header, Dock.Top);
		root.Children.Add(header);
		DockPanel.SetDock(columns, Dock.Top);
		root.Children.Add(columns);
		DockPanel.SetDock(footer, Dock.Bottom);
		root.Children.Add(footer);
		root.Children.Add(new Border
		{
			BorderThickness = new Thickness(1),
			BorderBrush = Ui.GroupBorderBrush,
			CornerRadius = new CornerRadius(4),
			Child = m_lbFiles
		});
		return root;
	}

	private static ColumnDefinitions FileColumns()
	{
		return new ColumnDefinitions("*,110,170");
	}

	private static Control BuildFileRow(RemoteEntry entry)
	{
		var row = new Grid { ColumnDefinitions = FileColumns() };
		AddToGrid(row, new TextBlock
		{
			Text = (entry.IsDirectory ? "📁  " : "📄  ") + entry.Name,
			TextTrimming = TextTrimming.CharacterEllipsis,
			FontWeight = entry.IsDirectory ? FontWeight.SemiBold : FontWeight.Normal
		}, 0);
		AddToGrid(row, new TextBlock
		{
			Text = entry.IsDirectory ? string.Empty : DeckStatus.FormatBytes(entry.Size),
			HorizontalAlignment = HorizontalAlignment.Right,
			Opacity = 0.8
		}, 1);
		AddToGrid(row, new TextBlock { Text = entry.Modified.ToString("dd.MM.yyyy HH:mm"), Opacity = 0.8, Margin = new Thickness(16, 0, 0, 0) }, 2);
		return row;
	}

	private static void AddToGrid(Grid grid, Control control, int column)
	{
		Grid.SetColumn(control, column);
		grid.Children.Add(control);
	}

	private async Task NavigateFilesAsync(string path)
	{
		DeckConnection? deck = m_deck;
		if (deck == null)
		{
			return;
		}

		string resolved = deck.ResolvePath(string.IsNullOrWhiteSpace(path) ? "~" : path);
		List<RemoteEntry>? entries = null;
		bool ok = await RunOnDeckAsync("Чтение папки…", (d, _) =>
		{
			SftpClient sftp = d.GetSftp();
			entries = sftp.ListDirectory(resolved)
				.Where(f => f.Name is not "." and not "..")
				.Select(ToEntry)
				.OrderByDescending(e => e.IsDirectory)
				.ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
				.ToList();
			return Task.CompletedTask;
		});

		if (!ok || entries == null)
		{
			return;
		}

		m_currentRemotePath = resolved;
		m_tbRemotePath.Text = resolved;
		m_lbFiles.ItemsSource = entries;
		SetStatus($"{resolved}: {entries.Count} элементов");
	}

	private static RemoteEntry ToEntry(ISftpFile file)
	{
		return new RemoteEntry(file.Name, file.FullName, file.IsDirectory, file.Length, file.LastWriteTime);
	}

	private List<RemoteEntry> SelectedEntries()
	{
		return m_lbFiles.SelectedItems?.OfType<RemoteEntry>().ToList() ?? [];
	}

	// ---------------------------------------------------------------- заливка

	private async Task UploadFilesAsync()
	{
		IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true, Title = "Файлы для загрузки на Deck" });
		await UploadLocalItemsAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
	}

	private async Task UploadFolderAsync()
	{
		IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false, Title = "Папка для загрузки на Deck" });
		await UploadLocalItemsAsync(folders.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
	}

	// Папки уходят целиком (с вложенными), файлы — в текущую папку Deck
	private async Task UploadLocalItemsAsync(IReadOnlyList<string> paths)
	{
		string? target = m_currentRemotePath;
		if (paths.Count == 0 || target == null)
		{
			return;
		}

		var files = new List<LocalFile>();
		foreach (string path in paths)
		{
			if (Directory.Exists(path))
			{
				string folderName = Path.GetFileName(path.TrimEnd('\\', '/'));
				files.AddRange(FolderSync.ListLocal(path, null).Select(f => f with { RelativePath = folderName + "/" + f.RelativePath }));
			}
			else if (File.Exists(path))
			{
				var info = new FileInfo(path);
				files.Add(new LocalFile(path, info.Name, info.Length, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds()));
			}
		}

		if (files.Count == 0)
		{
			return;
		}

		var progress = new Progress<SyncProgress>(ShowFilesProgress);
		m_filesProgress.IsVisible = true;
		bool ok = await RunOnDeckAsync($"Загрузка {files.Count} файлов в {target}…",
			(deck, ct) => FolderSync.UploadAsync(deck, target, files, GuessMode, progress, ct));
		m_filesProgress.IsVisible = false;

		if (ok)
		{
			m_filesProgressText.Text = $"Загружено файлов: {files.Count} ({DeckStatus.FormatBytes(files.Sum(f => f.Size))})";
			await NavigateFilesAsync(target);
		}
	}

	// Бит исполнения из Windows не приходит: ставим его скриптам и бинарникам Linux-сборок, остальным 644
	private static UnixFileMode GuessMode(LocalFile file)
	{
		string extension = Path.GetExtension(file.RelativePath).ToLowerInvariant();
		return extension is "" or ".sh" or ".x86_64" or ".appimage" or ".so" || extension.StartsWith(".so.", StringComparison.Ordinal)
			? FolderSync.ExecutableMode
			: FolderSync.RegularMode;
	}

	private void ShowFilesProgress(SyncProgress p)
	{
		m_filesProgress.Value = p.TotalBytes > 0 ? (double)p.DoneBytes / p.TotalBytes : 0;
		m_filesProgressText.Text = $"{p.DoneFiles}/{p.TotalFiles} файлов · {DeckStatus.FormatBytes(p.DoneBytes)} из {DeckStatus.FormatBytes(p.TotalBytes)}";
	}

	// ---------------------------------------------------------------- скачивание и правка

	private async Task DownloadSelectedAsync()
	{
		List<RemoteEntry> selected = SelectedEntries();
		if (selected.Count == 0)
		{
			return;
		}

		IStorageFolder? start = m_settings.LastDownloadFolder != null
			? await StorageProvider.TryGetFolderFromPathAsync(m_settings.LastDownloadFolder)
			: null;
		IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
		{
			AllowMultiple = false,
			Title = "Куда скачать",
			SuggestedStartLocation = start
		});
		string? localFolder = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
		if (localFolder == null)
		{
			return;
		}

		m_settings.LastDownloadFolder = localFolder;
		m_settings.Save();

		int count = 0;
		bool ok = await RunOnDeckAsync("Скачивание…", (deck, ct) =>
		{
			SftpClient sftp = deck.GetSftp();
			foreach (RemoteEntry entry in selected)
			{
				count += DownloadEntry(sftp, entry.FullPath, entry.IsDirectory, Path.Combine(localFolder, entry.Name), ct);
			}
			return Task.CompletedTask;
		});

		if (ok)
		{
			SetStatus($"Скачано файлов: {count} → {localFolder}");
		}
	}

	private static int DownloadEntry(SftpClient sftp, string remotePath, bool isDirectory, string localPath, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		if (!isDirectory)
		{
			using FileStream output = File.Create(localPath);
			sftp.DownloadFile(remotePath, output);
			return 1;
		}

		Directory.CreateDirectory(localPath);
		int count = 0;
		foreach (ISftpFile child in sftp.ListDirectory(remotePath))
		{
			if (child.Name is "." or ".." || child.IsSymbolicLink)
			{
				continue;
			}

			count += DownloadEntry(sftp, child.FullName, child.IsDirectory, Path.Combine(localPath, child.Name), ct);
		}

		return count;
	}

	private async Task CreateFolderAsync()
	{
		string? target = m_currentRemotePath;
		string? name = target != null ? await Dialogs.Prompt(this, "Новая папка", $"Имя папки в {target}:") : null;
		if (string.IsNullOrWhiteSpace(name))
		{
			return;
		}

		await RunCommandAsync("Создание папки…", (deck, ct) => deck.RunAsync($"mkdir -p {Sh.Quote(Sh.JoinPath(target!, name.Trim()))}", ct));
		await NavigateFilesAsync(target!);
	}

	private async Task RenameSelectedAsync()
	{
		List<RemoteEntry> selected = SelectedEntries();
		if (selected.Count != 1)
		{
			return;
		}

		RemoteEntry entry = selected[0];
		string? name = await Dialogs.Prompt(this, "Переименовать", "Новое имя:", entry.Name);
		if (string.IsNullOrWhiteSpace(name) || name == entry.Name)
		{
			return;
		}

		string destination = Sh.JoinPath(Sh.ParentPath(entry.FullPath), name.Trim());
		await RunCommandAsync("Переименование…", (deck, ct) => deck.RunAsync($"mv -n -- {Sh.Quote(entry.FullPath)} {Sh.Quote(destination)}", ct));
		await NavigateFilesAsync(m_currentRemotePath ?? "~");
	}

	private async Task DeleteSelectedAsync()
	{
		List<RemoteEntry> selected = SelectedEntries();
		if (selected.Count == 0)
		{
			return;
		}

		string names = string.Join("\n", selected.Take(10).Select(e => (e.IsDirectory ? "📁 " : "📄 ") + e.Name)) +
		               (selected.Count > 10 ? $"\n… и ещё {selected.Count - 10}" : string.Empty);
		if (!await Dialogs.YesNo(this, $"Удалить с Deck без возможности восстановления?\n\n{names}", "Удаление"))
		{
			return;
		}

		string paths = string.Join(' ', selected.Select(e => Sh.Quote(e.FullPath)));
		await RunCommandAsync("Удаление…", (deck, ct) => deck.RunAsync($"rm -rf -- {paths}", ct));
		await NavigateFilesAsync(m_currentRemotePath ?? "~");
	}
}

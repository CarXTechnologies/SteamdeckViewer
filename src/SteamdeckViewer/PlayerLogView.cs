using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using SteamdeckViewer.Core;

namespace SteamdeckViewer;

// Player.log с цветами по уровню записи (PlayerLogModel), поиском с подсветкой совпадений, фильтром по уровню
// и переходом к следующей ошибке или предупреждению. Строки приходят из любого потока и выводятся пачкой раз в 200 мс
internal sealed class PlayerLogView
{
	private const int MaxLines = 20000;

	private static readonly IBrush ErrorDark = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0x7B, 0x72));
	private static readonly IBrush ErrorLight = new ImmutableSolidColorBrush(Color.FromRgb(0xCF, 0x22, 0x2E));
	private static readonly IBrush ErrorFrameDark = new ImmutableSolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0x7B, 0x72));
	private static readonly IBrush ErrorFrameLight = new ImmutableSolidColorBrush(Color.FromArgb(0xB0, 0xCF, 0x22, 0x2E));
	private static readonly IBrush WarningDark = new ImmutableSolidColorBrush(Color.FromRgb(0xE3, 0xB3, 0x41));
	private static readonly IBrush WarningLight = new ImmutableSolidColorBrush(Color.FromRgb(0x9A, 0x67, 0x00));
	private static readonly IBrush WarningFrameDark = new ImmutableSolidColorBrush(Color.FromArgb(0xA0, 0xE3, 0xB3, 0x41));
	private static readonly IBrush WarningFrameLight = new ImmutableSolidColorBrush(Color.FromArgb(0xB0, 0x9A, 0x67, 0x00));
	private static readonly IBrush FrameDark = new ImmutableSolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
	private static readonly IBrush FrameLight = new ImmutableSolidColorBrush(Color.FromRgb(0x6E, 0x77, 0x81));
	private static readonly IBrush ToolDark = new ImmutableSolidColorBrush(Color.FromRgb(0x79, 0xC0, 0xFF));
	private static readonly IBrush ToolLight = new ImmutableSolidColorBrush(Color.FromRgb(0x09, 0x69, 0xDA));
	private static readonly IBrush MatchBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xD3, 0x3D));

	private readonly ConcurrentQueue<string> m_pending = new();
	private readonly PlayerLogModel m_model = new(MaxLines);

	// Строки модели в порядке строк документа: весь лог или только отобранные фильтром
	private readonly List<LogLine> m_shown = [];
	private readonly DispatcherTimer m_timer = new() { Interval = TimeSpan.FromMilliseconds(200) };

	private readonly TextEditor m_editor = new() { IsReadOnly = true, FontFamily = Ui.Mono, FontSize = 12 };
	private readonly TextBox m_tbSearch = new() { PlaceholderText = "Поиск (Ctrl+F): Enter — следующее, Shift+Enter — предыдущее" };
	private readonly TextBlock m_searchCount = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 60, Opacity = 0.75 };
	private readonly ComboBox m_cbLevel = new() { ItemsSource = new[] { "Все строки", "Предупреждения и ошибки", "Только ошибки" }, SelectedIndex = 0 };
	private readonly Button m_btnErrors = new();
	private readonly Button m_btnWarnings = new();

	private string m_query = string.Empty;
	private int m_matches;

	public PlayerLogView()
	{
		m_editor.Document.UndoStack.SizeLimit = 0;
		m_editor.Options.AllowScrollBelowDocument = false;
		m_editor.Background = Brushes.Transparent;
		// Выделение — цветом акцента, на нём красный и жёлтый текст не читается
		m_editor.TextArea.SelectionForeground = Brushes.White;
		// Первым, до раскраски выделения редактора: иначе цвет уровня перекрыл бы SelectionForeground
		m_editor.TextArea.TextView.LineTransformers.Insert(0, new Colorizer(this));
		var copy = new MenuItem { Header = "Копировать", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
		copy.Click += (_, _) => m_editor.Copy();
		var selectAll = new MenuItem { Header = "Выделить всё", InputGesture = new KeyGesture(Key.A, KeyModifiers.Control) };
		selectAll.Click += (_, _) => m_editor.SelectAll();
		m_editor.ContextMenu = new ContextMenu { Items = { copy, selectAll } };

		m_tbSearch.TextChanged += (_, _) =>
		{
			m_query = m_tbSearch.Text ?? string.Empty;
			m_matches = CountMatches(m_shown.Count);
			m_editor.TextArea.TextView.Redraw();
			FindNext(backward: false, fromSelectionStart: true);
			UpdateSearchCount();
		};
		m_tbSearch.KeyDown += (_, e) =>
		{
			if (e.Key == Key.Enter)
			{
				FindNext(backward: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
				e.Handled = true;
			}
			else if (e.Key == Key.Escape)
			{
				m_tbSearch.Text = string.Empty;
				e.Handled = true;
			}
		};

		m_cbLevel.SelectionChanged += (_, _) => Rebuild();
		m_btnErrors.Click += (_, _) => JumpToNext(LogLevel.Error);
		m_btnWarnings.Click += (_, _) => JumpToNext(LogLevel.Warning);
		ToolTip.SetTip(m_btnErrors, "К следующей ошибке после курсора");
		ToolTip.SetTip(m_btnWarnings, "К следующему предупреждению после курсора");
		UpdateCounters();

		var searchButtons = Ui.Row(Ui.Button("▲", () => FindNext(backward: true)), Ui.Button("▼", () => FindNext(backward: false)), m_searchCount);
		searchButtons.Margin = new Thickness(8, 0, 0, 0);
		var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
		DockPanel.SetDock(searchButtons, Dock.Right);
		searchRow.Children.Add(searchButtons);
		searchRow.Children.Add(m_tbSearch);

		var levelRow = new WrapPanel();
		foreach (Control control in new Control[] { m_cbLevel, m_btnErrors, m_btnWarnings })
		{
			control.Margin = new Thickness(0, 0, 8, 6);
			control.VerticalAlignment = VerticalAlignment.Center;
			levelRow.Children.Add(control);
		}

		var editorBorder = new Border
		{
			BorderThickness = new Thickness(1),
			BorderBrush = Ui.GroupBorderBrush,
			CornerRadius = new CornerRadius(4),
			ClipToBounds = true,
			Child = m_editor
		};

		var view = new DockPanel();
		DockPanel.SetDock(searchRow, Dock.Top);
		DockPanel.SetDock(levelRow, Dock.Top);
		view.Children.Add(searchRow);
		view.Children.Add(levelRow);
		view.Children.Add(editorBorder);
		view.KeyDown += (_, e) =>
		{
			if (e.Key == Key.F3)
			{
				FindNext(backward: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
				e.Handled = true;
			}
		};
		View = view;

		if (Application.Current != null)
		{
			Application.Current.ActualThemeVariantChanged += (_, _) =>
			{
				m_editor.TextArea.TextView.Redraw();
				UpdateCounters();
			};
		}

		m_timer.Tick += (_, _) => Flush();
		m_timer.Start();
	}

	public Control View { get; }

	private LogLevel MinimumLevel => m_cbLevel.SelectedIndex switch
	{
		1 => LogLevel.Warning,
		2 => LogLevel.Error,
		_ => LogLevel.Info
	};

	public void Append(string line)
	{
		m_pending.Enqueue(line);
	}

	public void Clear()
	{
		m_pending.Clear();
		m_model.Clear();
		m_shown.Clear();
		m_editor.Document.Text = string.Empty;
		m_matches = 0;
		UpdateCounters();
		UpdateSearchCount();
	}

	public void FocusSearch()
	{
		m_tbSearch.Focus();
		m_tbSearch.SelectAll();
	}

	private void Flush()
	{
		if (m_pending.IsEmpty)
		{
			return;
		}

		bool follow = IsAtEnd();
		while (m_pending.TryDequeue(out string? line))
		{
			m_model.Add(line);
		}

		Sync(follow);
	}

	private void Rebuild()
	{
		m_shown.Clear();
		m_editor.Document.Text = string.Empty;
		Sync(follow: true);
	}

	// Приводит документ к отбору модели: убирает вытесненные строки сверху и меняет только расходящийся хвост,
	// чтобы прокрутка и выделение не сбрасывались на каждой пачке строк
	private void Sync(bool follow)
	{
		TextDocument document = m_editor.Document;
		List<LogLine> next = m_model.Filter(MinimumLevel);

		int removed = 0;
		while (removed < m_shown.Count && m_shown[removed].IsRemoved)
		{
			removed++;
		}

		if (removed > 0)
		{
			document.Remove(0, removed == m_shown.Count ? document.TextLength : document.GetLineByNumber(removed + 1).Offset);
			m_shown.RemoveRange(0, removed);
		}

		int common = 0;
		while (common < m_shown.Count && common < next.Count && ReferenceEquals(m_shown[common], next[common]))
		{
			common++;
		}

		if (common < m_shown.Count)
		{
			int start = common == 0 ? 0 : document.GetLineByNumber(common).EndOffset;
			document.Remove(start, document.TextLength - start);
			m_shown.RemoveRange(common, m_shown.Count - common);
		}

		if (common < next.Count)
		{
			string text = string.Join('\n', next.Skip(common).Select(l => l.Text));
			document.Insert(document.TextLength, m_shown.Count == 0 ? text : "\n" + text);
			m_shown.AddRange(next.Skip(common));
		}

		// Сообщение над стеком перекрашивается, когда приходит стек
		m_editor.TextArea.TextView.Redraw();
		if (follow)
		{
			Dispatcher.UIThread.Post(m_editor.ScrollToEnd, DispatcherPriority.Background);
		}

		if (m_query.Length > 0)
		{
			m_matches = CountMatches(m_shown.Count);
			UpdateSearchCount();
		}

		UpdateCounters();
	}

	private bool IsAtEnd()
	{
		return m_editor.ExtentHeight <= m_editor.ViewportHeight || m_editor.VerticalOffset + m_editor.ViewportHeight >= m_editor.ExtentHeight - 20;
	}

	private void UpdateCounters()
	{
		bool dark = IsDark;
		int errors = m_model.Count(LogLevel.Error);
		int warnings = m_model.Count(LogLevel.Warning);
		SetCounter(m_btnErrors, $"Ошибки: {errors} ↓", errors, dark ? ErrorDark : ErrorLight, enabled: true);
		// Под фильтром «только ошибки» предупреждений в окне нет — переходить не к чему
		SetCounter(m_btnWarnings, $"Предупреждения: {warnings} ↓", warnings, dark ? WarningDark : WarningLight, MinimumLevel <= LogLevel.Warning);
	}

	private static void SetCounter(Button button, string text, int count, IBrush brush, bool enabled)
	{
		button.Content = text;
		button.IsEnabled = count > 0 && enabled;
		if (button.IsEnabled)
		{
			button.Foreground = brush;
		}
		else
		{
			button.ClearValue(Button.ForegroundProperty);
		}
	}

	// Следующая запись уровня после строки с курсором, по кругу
	private void JumpToNext(LogLevel level)
	{
		int count = m_shown.Count;
		if (count == 0)
		{
			return;
		}

		int current = m_editor.Document.GetLineByOffset(m_editor.CaretOffset).LineNumber - 1;
		for (int step = 1; step <= count; step++)
		{
			int i = (current + step) % count;
			LogLine line = m_shown[i];
			if (line.StartsEntry && line.Level == level && !line.IsTool)
			{
				DocumentLine documentLine = m_editor.Document.GetLineByNumber(i + 1);
				m_editor.Select(documentLine.Offset, documentLine.Length);
				m_editor.ScrollTo(i + 1, 1);
				return;
			}
		}
	}

	// Следующее совпадение после выделения (или с его начала — когда меняется запрос), по кругу
	private void FindNext(bool backward, bool fromSelectionStart = false)
	{
		int count = m_shown.Count;
		if (m_query.Length == 0 || count == 0)
		{
			return;
		}

		int origin = backward || fromSelectionStart ? m_editor.SelectionStart : m_editor.SelectionStart + m_editor.SelectionLength;
		DocumentLine originLine = m_editor.Document.GetLineByOffset(origin);
		int index = originLine.LineNumber - 1;
		int column = origin - originLine.Offset;

		for (int step = 0; step <= count; step++)
		{
			int i = backward ? ((index - step) % count + count) % count : (index + step) % count;
			IEnumerable<int> matches = PlayerLogModel.Matches(m_shown[i].Text, m_query);
			int found = step == 0
				? backward ? matches.LastOrDefault(m => m < column, -1) : matches.FirstOrDefault(m => m >= column, -1)
				: backward ? matches.LastOrDefault(-1) : matches.FirstOrDefault(-1);

			if (found >= 0)
			{
				m_editor.Select(m_editor.Document.GetLineByNumber(i + 1).Offset + found, m_query.Length);
				m_editor.ScrollTo(i + 1, found + 1);
				UpdateSearchCount();
				return;
			}
		}
	}

	// Совпадения в первых lines строках и ещё в строке lines до столбца lastColumn
	private int CountMatches(int lines, int lastColumn = 0)
	{
		int count = 0;
		for (int i = 0; i < lines && i < m_shown.Count; i++)
		{
			count += PlayerLogModel.Matches(m_shown[i].Text, m_query).Count();
		}

		return lines < m_shown.Count ? count + PlayerLogModel.Matches(m_shown[lines].Text, m_query).Count(m => m < lastColumn) : count;
	}

	// «3 из 17», когда выделено совпадение; номер — по числу совпадений до выделения
	private void UpdateSearchCount()
	{
		if (m_query.Length == 0)
		{
			m_searchCount.Text = string.Empty;
			return;
		}

		if (m_matches == 0)
		{
			m_searchCount.Text = "нет";
			return;
		}

		int start = m_editor.SelectionStart;
		bool onMatch = m_editor.SelectionLength == m_query.Length &&
		               string.Equals(m_editor.Document.GetText(start, m_query.Length), m_query, StringComparison.OrdinalIgnoreCase);
		if (!onMatch)
		{
			m_searchCount.Text = m_matches.ToString();
			return;
		}

		DocumentLine line = m_editor.Document.GetLineByOffset(start);
		int number = CountMatches(line.LineNumber - 1, start - line.Offset) + 1;
		m_searchCount.Text = $"{number} из {m_matches}";
	}

	private static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

	private static IBrush? BrushFor(LogLine line, bool dark)
	{
		if (line.IsTool)
		{
			return dark ? ToolDark : ToolLight;
		}

		return line.Level switch
		{
			LogLevel.Error => line.IsFrame ? dark ? ErrorFrameDark : ErrorFrameLight : dark ? ErrorDark : ErrorLight,
			LogLevel.Warning => line.IsFrame ? dark ? WarningFrameDark : WarningFrameLight : dark ? WarningDark : WarningLight,
			_ => line.IsFrame ? dark ? FrameDark : FrameLight : null
		};
	}

	private sealed class Colorizer(PlayerLogView owner) : DocumentColorizingTransformer
	{
		protected override void ColorizeLine(DocumentLine line)
		{
			int index = line.LineNumber - 1;
			if (index >= owner.m_shown.Count || line.Length == 0)
			{
				return;
			}

			LogLine log = owner.m_shown[index];
			IBrush? brush = BrushFor(log, IsDark);
			if (brush != null)
			{
				ChangeLinePart(line.Offset, line.EndOffset, e => e.TextRunProperties.SetForegroundBrush(brush));
			}

			string query = owner.m_query;
			foreach (int i in PlayerLogModel.Matches(log.Text, query).TakeWhile(i => i + query.Length <= line.Length))
			{
				ChangeLinePart(line.Offset + i, line.Offset + i + query.Length, e => e.TextRunProperties.SetBackgroundBrush(MatchBrush));
			}
		}
	}
}

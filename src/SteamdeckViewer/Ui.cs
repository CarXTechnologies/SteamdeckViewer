using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace SteamdeckViewer;

// Общие кирпичики разметки: группы с рамкой, формы «подпись — поле — кнопка», лог-окна
internal static class Ui
{
	public static readonly IBrush GroupBorderBrush = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128));
	public static readonly IBrush SplitterBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
	public static readonly IBrush OkBrush = new SolidColorBrush(Color.FromRgb(59, 165, 92));
	public static readonly IBrush WarnBrush = new SolidColorBrush(Color.FromRgb(232, 163, 61));
	public static readonly IBrush MutedBrush = new SolidColorBrush(Color.FromRgb(128, 128, 128));
	public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New, monospace");

	public static Border Group(string? header, Control content)
	{
		var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
		if (header != null)
		{
			var headerBlock = new TextBlock
			{
				Text = header,
				FontWeight = FontWeight.SemiBold,
				Margin = new Thickness(0, 0, 0, 8)
			};
			grid.Children.Add(headerBlock);
		}

		Grid.SetRow(content, 1);
		grid.Children.Add(content);

		return new Border
		{
			BorderThickness = new Thickness(1),
			BorderBrush = GroupBorderBrush,
			CornerRadius = new CornerRadius(4),
			Padding = new Thickness(10),
			Child = grid
		};
	}

	public static Button Button(string text, Action onClick)
	{
		var button = new Button { Content = text, VerticalAlignment = VerticalAlignment.Center };
		button.Click += (_, _) => onClick();
		return button;
	}

	public static Button Button(string text, Func<Task> onClick)
	{
		var button = new Button { Content = text, VerticalAlignment = VerticalAlignment.Center };
		button.Click += async (_, _) => await onClick();
		return button;
	}

	public static StackPanel Row(params Control[] children)
	{
		var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
		foreach (Control child in children)
		{
			row.Children.Add(child);
		}

		return row;
	}

	public static StackPanel Column(double spacing, params Control[] children)
	{
		var column = new StackPanel { Spacing = spacing };
		foreach (Control child in children)
		{
			column.Children.Add(child);
		}

		return column;
	}

	public static TextBlock Hint(string text)
	{
		return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
	}

	// Ограничение, которое легко пропустить: не приглушено, в отличие от Hint
	public static TextBlock Note(string text)
	{
		return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
	}

	public static TextBox LogBox()
	{
		var box = new TextBox
		{
			IsReadOnly = true,
			AcceptsReturn = true,
			TextWrapping = TextWrapping.NoWrap,
			FontFamily = Mono,
			FontSize = 12
		};
		ScrollViewer.SetHorizontalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
		ScrollViewer.SetVerticalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
		return box;
	}
}

// Сетка формы: подпись слева, поле по центру, необязательная кнопка справа
internal sealed class FormGrid
{
	private int m_row;

	public Grid Grid { get; } = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

	public void Add(string label, Control field, Control? extra = null)
	{
		Grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

		var text = new TextBlock
		{
			Text = label,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0, 0, 12, 6)
		};
		Grid.SetRow(text, m_row);
		Grid.Children.Add(text);

		field.Margin = new Thickness(0, 0, 0, 6);
		Grid.SetRow(field, m_row);
		Grid.SetColumn(field, 1);
		if (extra == null)
		{
			Grid.SetColumnSpan(field, 2);
		}
		Grid.Children.Add(field);

		if (extra != null)
		{
			extra.Margin = new Thickness(8, 0, 0, 6);
			Grid.SetRow(extra, m_row);
			Grid.SetColumn(extra, 2);
			Grid.Children.Add(extra);
		}

		m_row++;
	}

	public void AddFull(Control control)
	{
		Grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		control.Margin = new Thickness(0, 0, 0, 6);
		Grid.SetRow(control, m_row);
		Grid.SetColumnSpan(control, 3);
		Grid.Children.Add(control);
		m_row++;
	}
}

// Лог с добавлением из любого потока: строки копятся в очереди и выводятся пачкой раз в 200 мс,
// в окне держится не больше MaxLines последних строк
internal sealed class LogView
{
	private const int MaxLines = 4000;

	private readonly ConcurrentQueue<string> m_pending = new();
	private readonly LinkedList<string> m_lines = new();
	private readonly DispatcherTimer m_timer = new() { Interval = TimeSpan.FromMilliseconds(200) };

	public TextBox Box { get; } = Ui.LogBox();

	public LogView()
	{
		m_timer.Tick += (_, _) => Flush();
		m_timer.Start();
	}

	public void Append(string line)
	{
		m_pending.Enqueue(line);
	}

	public void Clear()
	{
		m_pending.Clear();
		m_lines.Clear();
		Box.Text = string.Empty;
	}

	public string Text => string.Join('\n', m_lines);

	private void Flush()
	{
		if (m_pending.IsEmpty)
		{
			return;
		}

		while (m_pending.TryDequeue(out string? line))
		{
			m_lines.AddLast(line);
			if (m_lines.Count > MaxLines)
			{
				m_lines.RemoveFirst();
			}
		}

		Box.Text = Text;
		Box.CaretIndex = Box.Text.Length;
	}
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SteamdeckViewer.Core;

namespace SteamdeckViewer;

// Модальные окна: сообщения, да/нет, ввод строки и пароля, редактирование устройства
internal static class Dialogs
{
	public static Task Error(Window owner, string message)
	{
		return ShowMessageAsync(owner, message, "Ошибка", "OK", null);
	}

	public static Task Info(Window owner, string message, string caption = AppInfo.Name)
	{
		return ShowMessageAsync(owner, message, caption, "OK", null);
	}

	public static Task<bool> YesNo(Window owner, string message, string caption)
	{
		return ShowMessageAsync(owner, message, caption, "Да", "Нет");
	}

	// Вопрос со своими подписями кнопок; true — нажата первая
	public static Task<bool> Ask(Window owner, string message, string caption, string accept, string decline)
	{
		return ShowMessageAsync(owner, message, caption, accept, decline);
	}

	public static async Task<string?> Prompt(Window owner, string caption, string label, string initial = "", bool password = false)
	{
		var box = new TextBox { Text = initial, PasswordChar = password ? '•' : default };
		var dlg = CreateDialog(owner, caption, 440);

		Grid root = BuildBody(Ui.Column(6, new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, box),
			OkCancel(dlg, () => dlg.Close(box.Text ?? string.Empty)));
		dlg.Content = root;
		dlg.Opened += (_, _) =>
		{
			box.Focus();
			box.SelectAll();
		};

		return await dlg.ShowDialog<string?>(owner);
	}

	public static async Task<bool> EditDevice(Window owner, DeckDevice device, string caption)
	{
		var name = new TextBox { Text = device.Name };
		var host = new TextBox { Text = device.Host, PlaceholderText = "steamdeck.local или 192.168.1.50" };
		var user = new TextBox { Text = device.User };
		var port = new NumericUpDown { Value = device.SshPort, Minimum = 1, Maximum = 65535, FormatString = "0" };

		var form = new FormGrid();
		form.Add("Название", name);
		form.Add("Адрес (IP или имя)", host);
		form.Add("Пользователь", user);
		form.Add("SSH-порт", port);

		var dlg = CreateDialog(owner, caption, 480);
		dlg.Content = BuildBody(form.Grid, OkCancel(dlg, () =>
		{
			if (string.IsNullOrWhiteSpace(host.Text))
			{
				return;
			}

			device.Name = string.IsNullOrWhiteSpace(name.Text) ? host.Text.Trim() : name.Text.Trim();
			device.Host = host.Text.Trim();
			device.User = string.IsNullOrWhiteSpace(user.Text) ? "deck" : user.Text.Trim();
			device.SshPort = (int)(port.Value ?? 22);
			dlg.Close(true);
		}));

		// Отмена закрывает окно с null, поэтому результат nullable
		return await dlg.ShowDialog<bool?>(owner) == true;
	}

	// decline == null — одна кнопка, она же закрывает окно по Esc
	private static async Task<bool> ShowMessageAsync(Window owner, string message, string caption, string accept, string? decline)
	{
		var dlg = CreateDialog(owner, caption, 520);
		var text = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap };

		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
		var yes = new Button { Content = accept, MinWidth = 90, IsDefault = true, IsCancel = decline == null };
		yes.Click += (_, _) => dlg.Close(true);
		buttons.Children.Add(yes);
		if (decline != null)
		{
			var no = new Button { Content = decline, MinWidth = 90, IsCancel = true };
			no.Click += (_, _) => dlg.Close(false);
			buttons.Children.Add(no);
		}

		dlg.Content = BuildBody(new ScrollViewer { Content = text, MaxHeight = 480 }, buttons);
		return await dlg.ShowDialog<bool?>(owner) == true;
	}

	private static Window CreateDialog(Window owner, string caption, double width)
	{
		return new Window
		{
			Title = caption,
			Width = width,
			SizeToContent = SizeToContent.Height,
			CanResize = false,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			ShowInTaskbar = false,
			Icon = owner.Icon
		};
	}

	private static StackPanel OkCancel(Window dlg, Action onOk)
	{
		var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true };
		var cancel = new Button { Content = "Отмена", MinWidth = 90, IsCancel = true };
		ok.Click += (_, _) => onOk();
		cancel.Click += (_, _) => dlg.Close(null);

		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
		buttons.Children.Add(ok);
		buttons.Children.Add(cancel);
		return buttons;
	}

	private static Grid BuildBody(Control content, Control buttons)
	{
		var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(16) };
		Grid.SetRow(content, 0);
		root.Children.Add(content);

		buttons.Margin = new Thickness(0, 16, 0, 0);
		Grid.SetRow(buttons, 1);
		root.Children.Add(buttons);
		return root;
	}
}

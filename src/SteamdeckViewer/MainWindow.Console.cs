using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SteamdeckViewer.Core;

namespace SteamdeckViewer;

// Вкладка «Консоль»: разовые команды без интерактивного ввода. Для sudo и интерактивных программ — «SSH-терминал»
internal sealed partial class MainWindow
{
	private readonly TextBox m_tbCommand = new() { FontFamily = Ui.Mono, PlaceholderText = "например: journalctl --user -n 50  ·  ls ~/devkit-game  ·  df -h" };
	private readonly LogView m_consoleLog = new();
	private readonly List<string> m_commandHistory = [];
	private int m_historyIndex;

	private Control BuildConsoleTab()
	{
		m_tbCommand.KeyDown += async (_, e) =>
		{
			switch (e.Key)
			{
				case Key.Enter:
					await RunConsoleCommandAsync();
					break;
				case Key.Up when m_commandHistory.Count > 0:
					m_historyIndex = Math.Max(0, m_historyIndex - 1);
					m_tbCommand.Text = m_commandHistory[m_historyIndex];
					m_tbCommand.CaretIndex = m_tbCommand.Text.Length;
					break;
				case Key.Down when m_commandHistory.Count > 0:
					m_historyIndex = Math.Min(m_commandHistory.Count, m_historyIndex + 1);
					m_tbCommand.Text = m_historyIndex < m_commandHistory.Count ? m_commandHistory[m_historyIndex] : string.Empty;
					m_tbCommand.CaretIndex = m_tbCommand.Text.Length;
					break;
			}
		};

		var input = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 8) };
		input.Children.Add(m_tbCommand);
		Button run = Ui.Button("Выполнить", RunConsoleCommandAsync);
		run.Margin = new Thickness(8, 0, 0, 0);
		Grid.SetColumn(run, 1);
		input.Children.Add(run);
		Button clear = Ui.Button("Очистить", () => m_consoleLog.Clear());
		clear.Margin = new Thickness(8, 0, 0, 0);
		Grid.SetColumn(clear, 2);
		input.Children.Add(clear);

		var root = new DockPanel { Margin = new Thickness(0, 10, 0, 10) };
		DockPanel.SetDock(input, Dock.Top);
		root.Children.Add(input);
		root.Children.Add(m_consoleLog.Box);
		return root;
	}

	private async Task RunConsoleCommandAsync()
	{
		string command = m_tbCommand.Text?.Trim() ?? string.Empty;
		if (command.Length == 0)
		{
			return;
		}

		m_commandHistory.Remove(command);
		m_commandHistory.Add(command);
		m_historyIndex = m_commandHistory.Count;
		m_tbCommand.Text = string.Empty;

		m_consoleLog.Append("$ " + command);
		CommandResult? result = null;
		await RunOnDeckAsync("Выполнение…", async (deck, ct) => result = await deck.RunAsync(command, ct));

		if (result != null)
		{
			foreach (string line in result.Combined.TrimEnd('\n').Split('\n'))
			{
				m_consoleLog.Append(line);
			}

			m_consoleLog.Append($"[код {result.ExitCode}]");
			m_consoleLog.Append(string.Empty);
			SetStatus($"Команда завершилась с кодом {result.ExitCode}");
		}
	}
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace SteamdeckViewer.Core;

// Что Unity-плеер сообщает о себе в Player.log: адрес, порт отладчика Mono и анонс для IDE
public sealed record UnityPlayerInfo(string? Ip, int? DebuggerPort, string? ProjectName, string? Announce);

public static partial class UnityPlayerLog
{
	// "Multi-casting "[IP] 10.0.1.152 [Port] 55000 ... [Guid] 123 ... [ProjectName] X" to [225.0.0.222:54997]..."
	[GeneratedRegex("Multi-casting \"(?<msg>.+?)\" to \\[225\\.0\\.0\\.222:\\d+\\]")]
	private static partial Regex MulticastRegex();

	[GeneratedRegex(@"managed debugger on port (?<port>\d+)", RegexOptions.IgnoreCase)]
	private static partial Regex DebuggerPortRegex();

	[GeneratedRegex(@"\[IP\] (?<v>\S+)")]
	private static partial Regex IpRegex();

	[GeneratedRegex(@"\[Guid\] (?<v>\d+)")]
	private static partial Regex GuidRegex();

	[GeneratedRegex(@"\[ProjectName\] (?<v>.+?)(?: \[|$)")]
	private static partial Regex ProjectRegex();

	[GeneratedRegex(@"\[Debug\] (?<v>\d)")]
	private static partial Regex DebugRegex();

	// Команда для поиска уже записанных строк в логе (без слежения)
	public static string GrepCommand(string logPath)
	{
		return $"grep -a -E 'Multi-casting|managed debugger on port' {Sh.Path(logPath)} 2>/dev/null | tail -n 20";
	}

	// Возвращает обновлённые сведения, если строка о плеере; иначе null
	public static UnityPlayerInfo? Parse(string line, UnityPlayerInfo? current)
	{
		Match port = DebuggerPortRegex().Match(line);
		if (port.Success)
		{
			return (current ?? new UnityPlayerInfo(null, null, null, null)) with { DebuggerPort = int.Parse(port.Groups["port"].Value) };
		}

		Match multicast = MulticastRegex().Match(line);
		if (!multicast.Success)
		{
			return null;
		}

		string message = multicast.Groups["msg"].Value;
		int? debuggerPort = current?.DebuggerPort;

		// Порт отладчика плеера: 56000 + Guid % 1000 (документация Unity по managed debugging)
		Match guid = GuidRegex().Match(message);
		Match debug = DebugRegex().Match(message);
		if (debuggerPort == null && guid.Success && debug is { Success: true } && debug.Groups["v"].Value == "1" &&
		    long.TryParse(guid.Groups["v"].Value, out long guidValue))
		{
			debuggerPort = 56000 + (int)(guidValue % 1000);
		}

		Match ip = IpRegex().Match(message);
		Match project = ProjectRegex().Match(message);
		return new UnityPlayerInfo(
			ip.Success ? ip.Groups["v"].Value : current?.Ip,
			debuggerPort,
			project.Success ? project.Groups["v"].Value.Trim() : current?.ProjectName,
			message);
	}
}

// Rider находит Unity-плееры по UDP-анонсам на 225.0.0.222. По Wi-Fi multicast от Deck часто не доходит,
// поэтому повторяем последний анонс плеера в сети ПК — с адресом Deck, по которому мы к нему подключены.
// Шлём через loopback и через адаптер, которым ПК достаёт до Deck: при включённом VPN multicast по умолчанию
// уходит в туннель (у VPN-адаптера метрика ниже), и Rider на этом же ПК анонс бы не увидел
public sealed class UnityAnnounceRelay : IDisposable
{
	private static readonly IPAddress Group = IPAddress.Parse("225.0.0.222");
	private static readonly int[] Ports = [54997, 34997, 57997, 58997];

	private readonly System.Threading.Timer m_timer;
	private readonly Lock m_lock = new();
	private List<UdpClient> m_senders = [];
	private byte[]? m_packet;

	public UnityAnnounceRelay()
	{
		m_timer = new System.Threading.Timer(_ => Send(), null, Timeout.Infinite, Timeout.Infinite);
	}

	public bool IsRunning { get; private set; }

	public void Start(string announce, string deckAddress)
	{
		string message = announce;
		IPAddress? deck = IPAddress.TryParse(deckAddress, out IPAddress? ip) && ip.AddressFamily == AddressFamily.InterNetwork ? ip : null;
		if (deck != null)
		{
			message = Regex.Replace(message, @"\[IP\] \S+", "[IP] " + deckAddress);
		}

		lock (m_lock)
		{
			CloseSenders();
			m_senders = CreateSenders(deck);
			m_packet = Encoding.ASCII.GetBytes(message);
		}

		IsRunning = true;
		m_timer.Change(0, 1000);
	}

	public void Stop()
	{
		IsRunning = false;
		m_timer.Change(Timeout.Infinite, Timeout.Infinite);
		lock (m_lock)
		{
			CloseSenders();
		}
	}

	private static List<UdpClient> CreateSenders(IPAddress? deck)
	{
		var interfaces = new List<IPAddress> { IPAddress.Loopback };
		IPAddress? towardsDeck = deck != null ? LocalAddressTowards(deck) : null;
		if (towardsDeck != null && !IPAddress.IsLoopback(towardsDeck))
		{
			interfaces.Add(towardsDeck);
		}

		var senders = new List<UdpClient>();
		foreach (IPAddress local in interfaces)
		{
			try
			{
				var udp = new UdpClient(AddressFamily.InterNetwork) { MulticastLoopback = true, Ttl = 1 };
				udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
				senders.Add(udp);
			}
			catch (SocketException)
			{
				// адаптер мог исчезнуть
			}
		}

		return senders;
	}

	// UDP-«подключение» ничего не отправляет: система лишь выбирает маршрут и локальный адрес до Deck
	private static IPAddress? LocalAddressTowards(IPAddress deck)
	{
		try
		{
			using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
			probe.Connect(deck, 9);
			return (probe.LocalEndPoint as IPEndPoint)?.Address;
		}
		catch (SocketException)
		{
			return null;
		}
	}

	private void Send()
	{
		lock (m_lock)
		{
			if (m_packet == null)
			{
				return;
			}

			foreach (UdpClient sender in m_senders)
			{
				foreach (int port in Ports)
				{
					try
					{
						sender.Send(m_packet, m_packet.Length, new IPEndPoint(Group, port));
					}
					catch
					{
						// сеть могла пропасть — попробуем на следующем тике
					}
				}
			}
		}
	}

	private void CloseSenders()
	{
		foreach (UdpClient sender in m_senders)
		{
			sender.Dispose();
		}

		m_senders = [];
	}

	public void Dispose()
	{
		m_timer.Dispose();
		lock (m_lock)
		{
			CloseSenders();
		}
	}
}

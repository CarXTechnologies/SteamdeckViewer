using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SteamdeckViewer.Core;

public sealed record LocalNetwork(string InterfaceName, IPAddress Address, int PrefixLength, IPAddress? Gateway = null)
{
	public override string ToString()
	{
		return $"{Network}/{PrefixLength} ({InterfaceName})";
	}

	public IPAddress Network
	{
		get
		{
			uint mask = PrefixLength == 0 ? 0 : uint.MaxValue << (32 - PrefixLength);
			return ToAddress(ToUInt(Address) & mask);
		}
	}

	internal static uint ToUInt(IPAddress address)
	{
		byte[] b = address.GetAddressBytes();
		return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
	}

	internal static IPAddress ToAddress(uint value)
	{
		return new IPAddress([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
	}
}

// Сетевые адаптеры ПК и признаки того, что локальную сеть режет VPN
public static class NetworkDiagnostics
{
	private static readonly string[] VirtualMarkers =
	[
		"WireGuard", "Wintun", "TAP-", "OpenVPN", "VPN", "Tunnel", "Amnezia", "Hyper-V", "Virtual", "VMware", "VirtualBox",
		"Loopback", "Cisco AnyConnect", "Fortinet", "PANGP", "Tailscale", "ZeroTier"
	];

	private static readonly string[] VpnMarkers =
	[
		"WireGuard", "Wintun", "TAP-", "OpenVPN", "VPN", "Amnezia", "Cisco AnyConnect", "Fortinet", "PANGP", "Tailscale", "ZeroTier"
	];

	// Физические Ethernet и Wi-Fi адаптеры с IPv4: через них идут mDNS-запросы и поиск по подсети
	public static IReadOnlyList<LocalNetwork> PhysicalNetworks()
	{
		var result = new List<LocalNetwork>();
		foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
		{
			if (nic.OperationalStatus != OperationalStatus.Up ||
			    nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet) ||
			    Matches(nic, VirtualMarkers))
			{
				continue;
			}

			IPInterfaceProperties properties = nic.GetIPProperties();
			IPAddress? gateway = properties.GatewayAddresses
				.Select(g => g.Address)
				.FirstOrDefault(g => g.AddressFamily == AddressFamily.InterNetwork && !g.Equals(IPAddress.Any));

			foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
			{
				if (address.Address.AddressFamily == AddressFamily.InterNetwork)
				{
					result.Add(new LocalNetwork(nic.Name, address.Address, address.PrefixLength, gateway));
				}
			}
		}

		return result;
	}

	// Все адреса ПК в том виде, в каком их записывает удалённая сторона (без %scope у IPv6)
	public static IReadOnlySet<string> LocalAddresses()
	{
		var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
		{
			if (nic.OperationalStatus != OperationalStatus.Up)
			{
				continue;
			}

			foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
			{
				string text = address.Address.ToString();
				int scope = text.IndexOf('%');
				result.Add(scope < 0 ? text : text[..scope]);
			}
		}

		return result;
	}

	public static IReadOnlyList<string> ActiveVpnAdapters()
	{
		return NetworkInterface.GetAllNetworkInterfaces()
			.Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
			              (nic.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel || Matches(nic, VpnMarkers)))
			.Select(nic => nic.Name)
			.ToList();
	}

	// Так Windows отвечает на соединение, которое запретил фаервол (WFP): у VPN это kill switch
	public static bool IsBlockedByFirewall(Exception? e)
	{
		for (; e != null; e = e.InnerException)
		{
			if (e is SocketException { SocketErrorCode: SocketError.AccessDenied })
			{
				return true;
			}
		}

		return false;
	}

	// Multicast kill switch выбрасывает молча, а TCP к шлюзу локальной сети сразу получает AccessDenied —
	// по нему и понимаем, что локальную сеть режет VPN
	public static async Task<bool> IsLocalNetworkBlockedAsync(CancellationToken ct)
	{
		List<IPAddress> gateways = PhysicalNetworks().Select(n => n.Gateway).OfType<IPAddress>().Distinct().ToList();
		bool[] blocked = await Task.WhenAll(gateways.Select(g => IsBlockedAsync(g.ToString(), 53, ct)));
		return blocked.Any(b => b);
	}

	public static async Task<bool> IsBlockedAsync(string host, int port, CancellationToken ct)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(TimeSpan.FromSeconds(2));
		using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
		try
		{
			await socket.ConnectAsync(host, port, timeout.Token);
			return false;
		}
		catch (Exception e)
		{
			return IsBlockedByFirewall(e);
		}
	}

	public static string VpnAdvice(string? deckHost)
	{
		IReadOnlyList<string> vpn = ActiveVpnAdapters();
		string who = vpn.Count > 0 ? $"VPN ({string.Join(", ", vpn)})" : "фаервол или VPN";
		string subnet = IPAddress.TryParse(deckHost, out IPAddress? ip) && ip.AddressFamily == AddressFamily.InterNetwork
			? $"{ip.GetAddressBytes()[0]}.{ip.GetAddressBytes()[1]}.0.0/16"
			: "10.0.0.0/8";

		return
			$"Windows не пускает соединение в локальную сеть: его блокирует {who}. " +
			"У клиентов на WireGuard/AmneziaWG при маршруте всего трафика (0.0.0.0/0) включается kill switch, который режет локальную сеть целиком.\n\n" +
			"Исправить один раз, не выключая VPN (AmneziaVPN), — любой из вариантов:\n" +
			$"• Раздельное туннелирование приложений → добавить {Environment.ProcessPath ?? "exe этого приложения"} " +
			"(после переименования или переноса exe — заново).\n" +
			"• Раздельное туннелирование по адресам → режим «Адреса из списка не должны открываться через VPN» → " +
			$"добавить {subnet} (или все локальные: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16) и включить.\n\n" +
			"Если VPN подключён конфигом WireGuard/AmneziaWG напрямую: в AllowedIPs замените 0.0.0.0/0 на 0.0.0.0/1, 128.0.0.0/1 — " +
			"kill switch не включится, а весь интернет по-прежнему пойдёт через VPN.";
	}

	private static bool Matches(NetworkInterface nic, string[] markers)
	{
		return markers.Any(m => nic.Description.Contains(m, StringComparison.OrdinalIgnoreCase) ||
		                        nic.Name.Contains(m, StringComparison.OrdinalIgnoreCase));
	}
}

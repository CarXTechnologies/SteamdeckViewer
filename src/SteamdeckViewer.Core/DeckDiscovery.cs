using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SteamdeckViewer.Core;

public sealed record DiscoveredDeck(string Name, string Address, int HttpPort, string? Login);

public sealed record DiscoveryResult(IReadOnlyList<DiscoveredDeck> Decks, bool BlockedByFirewall);

// Поиск устройств в режиме разработчика. Служба devkit анонсирует себя по mDNS как _steamos-devkit._tcp.
// Запрос уходит с каждого физического адаптера отдельно (у ПК бывает и Ethernet, и Wi-Fi в разных подсетях),
// с временного порта: так Deck отвечает unicast прямо нам (RFC 6762, 6.7), и ответ не теряется,
// даже если сеть фильтрует multicast к клиентам. Если mDNS молчит, можно проверить подсети на порт службы
public static class DeckDiscovery
{
	private const string ServiceName = "_steamos-devkit._tcp.local";
	private const ushort TypeA = 1;
	private const ushort TypePtr = 12;
	private const ushort TypeTxt = 16;
	private const ushort TypeSrv = 33;

	private static readonly IPEndPoint MdnsGroup = new(IPAddress.Parse("224.0.0.251"), 5353);

	public static async Task<DiscoveryResult> FindAsync(TimeSpan scanTime, CancellationToken ct)
	{
		var found = new ConcurrentDictionary<string, DiscoveredDeck>(StringComparer.OrdinalIgnoreCase);
		bool blocked = false;

		await Task.WhenAll(NetworkDiagnostics.PhysicalNetworks().Select(async network =>
		{
			try
			{
				await QueryAsync(network.Address, scanTime, found, ct);
			}
			catch (Exception e) when (NetworkDiagnostics.IsBlockedByFirewall(e))
			{
				blocked = true;
			}
			catch (SocketException)
			{
				// адаптер без маршрута для multicast — пропускаем
			}
		}));

		if (found.IsEmpty && !blocked)
		{
			blocked = await NetworkDiagnostics.IsLocalNetworkBlockedAsync(ct);
		}

		return new DiscoveryResult(found.Values.ToList(), blocked && found.IsEmpty);
	}

	// Подсети физических адаптеров, которые имеет смысл перебирать: не шире /22, без link-local 169.254/16
	public static IReadOnlyList<LocalNetwork> ScannableNetworks()
	{
		return NetworkDiagnostics.PhysicalNetworks()
			.Where(n => n.Address.GetAddressBytes() is not [169, 254, ..])
			.Select(n => n.PrefixLength < 22 ? n with { PrefixLength = 24 } : n)
			.DistinctBy(n => (n.Network.ToString(), n.PrefixLength))
			.ToList();
	}

	// Запасной путь: TCP к порту службы devkit на каждом адресе подсети, затем /properties.json у ответивших
	public static async Task<DiscoveryResult> ScanAsync(IReadOnlyList<LocalNetwork> networks, IProgress<double>? progress, CancellationToken ct)
	{
		var own = new HashSet<string>(networks.Select(n => n.Address.ToString()));
		List<IPAddress> hosts = networks.SelectMany(HostsOf).Where(h => !own.Contains(h.ToString())).ToList();

		var found = new ConcurrentBag<DiscoveredDeck>();
		int done = 0;
		bool blocked = false;
		using var gate = new SemaphoreSlim(64);

		await Task.WhenAll(hosts.Select(async host =>
		{
			await gate.WaitAsync(ct);
			try
			{
				if (await IsPortOpenAsync(host, DevkitService.DefaultPort, ct))
				{
					DevkitProperties? properties = await DevkitService.GetPropertiesAsync(host.ToString(), DevkitService.DefaultPort, ct);
					if (properties != null)
					{
						found.Add(new DiscoveredDeck($"Steam Deck {host}", host.ToString(), DevkitService.DefaultPort, properties.Login));
					}
				}
			}
			catch (Exception e) when (NetworkDiagnostics.IsBlockedByFirewall(e))
			{
				blocked = true;
			}
			finally
			{
				gate.Release();
				progress?.Report((double)Interlocked.Increment(ref done) / hosts.Count);
			}
		}));

		return new DiscoveryResult(found.ToList(), blocked && found.IsEmpty);
	}

	private static async Task QueryAsync(IPAddress local, TimeSpan scanTime, ConcurrentDictionary<string, DiscoveredDeck> found, CancellationToken ct)
	{
		using var udp = new UdpClient(new IPEndPoint(local, 0));
		udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
		udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(scanTime);

		byte[] query = BuildQuery();
		await udp.SendAsync(query, MdnsGroup, timeout.Token);

		// Повтор через секунду: первый пакет в Wi-Fi нередко теряется
		_ = Task.Delay(1000, timeout.Token).ContinueWith(async t =>
		{
			if (!t.IsCanceled)
			{
				try
				{
					await udp.SendAsync(query, MdnsGroup, timeout.Token);
				}
				catch
				{
					// сокет уже закрыт
				}
			}
		}, TaskScheduler.Default);

		try
		{
			while (true)
			{
				UdpReceiveResult response = await udp.ReceiveAsync(timeout.Token);
				foreach (DiscoveredDeck deck in ParseResponse(response.Buffer, response.RemoteEndPoint.Address))
				{
					found[deck.Address] = deck;
				}
			}
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			// время поиска вышло
		}
	}

	// Запрос PTR для _steamos-devkit._tcp.local с битом «ответить unicast»
	internal static byte[] BuildQuery()
	{
		var packet = new List<byte>(new byte[12]);
		packet[5] = 1; // QDCOUNT = 1
		foreach (string label in ServiceName.Split('.'))
		{
			packet.Add((byte)label.Length);
			packet.AddRange(Encoding.ASCII.GetBytes(label));
		}

		packet.AddRange([0, 0, (byte)TypePtr, 0x80, 0x01]);
		return packet.ToArray();
	}

	internal static IReadOnlyList<DiscoveredDeck> ParseResponse(byte[] data, IPAddress sender)
	{
		if (data.Length < 12)
		{
			return [];
		}

		var instances = new List<string>();
		var ports = new Dictionary<string, (int Port, string Target)>(StringComparer.OrdinalIgnoreCase);
		var logins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var addresses = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);

		try
		{
			int questions = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
			int records = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6)) +
			              BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8)) +
			              BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(10));
			int offset = 12;

			for (int i = 0; i < questions; i++)
			{
				ReadName(data, ref offset);
				offset += 4;
			}

			for (int i = 0; i < records; i++)
			{
				string owner = ReadName(data, ref offset);
				ushort type = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
				int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 8));
				int rdata = offset + 10;
				offset = rdata + length;

				switch (type)
				{
					case TypePtr when owner.Equals(ServiceName, StringComparison.OrdinalIgnoreCase):
						int ptr = rdata;
						instances.Add(ReadName(data, ref ptr));
						break;
					case TypeSrv:
						int target = rdata + 6;
						ports[owner] = (BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(rdata + 4)), ReadName(data, ref target));
						break;
					case TypeTxt:
						foreach (string entry in ReadTxt(data, rdata, length))
						{
							if (entry.StartsWith("login=", StringComparison.Ordinal))
							{
								logins[owner] = entry[6..];
							}
						}
						break;
					case TypeA when length == 4:
						addresses[owner] = new IPAddress(data.AsSpan(rdata, 4));
						break;
				}
			}
		}
		catch (ArgumentOutOfRangeException)
		{
			// обрезанный пакет: берём то, что успели разобрать
		}
		catch (IndexOutOfRangeException)
		{
			// то же
		}

		var result = new List<DiscoveredDeck>();
		foreach (string instance in instances.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			int port = DevkitService.DefaultPort;
			IPAddress address = sender;
			if (ports.TryGetValue(instance, out (int Port, string Target) srv))
			{
				port = srv.Port;
				if (addresses.TryGetValue(srv.Target, out IPAddress? a))
				{
					address = a;
				}
			}

			string name = instance.EndsWith("." + ServiceName, StringComparison.OrdinalIgnoreCase)
				? instance[..^(ServiceName.Length + 1)]
				: instance;
			result.Add(new DiscoveredDeck(name, address.ToString(), port, logins.GetValueOrDefault(instance)));
		}

		return result;
	}

	// Имена DNS со сжатием (RFC 1035, 4.1.4); offset сдвигается только по несжатой части
	private static string ReadName(byte[] data, ref int offset)
	{
		var labels = new List<string>();
		int position = offset;
		bool jumped = false;

		for (int guard = 0; guard < 128; guard++)
		{
			byte length = data[position];
			if (length == 0)
			{
				position++;
				break;
			}

			if ((length & 0xC0) == 0xC0)
			{
				int pointer = ((length & 0x3F) << 8) | data[position + 1];
				if (!jumped)
				{
					offset = position + 2;
				}

				jumped = true;
				position = pointer;
				continue;
			}

			labels.Add(Encoding.UTF8.GetString(data, position + 1, length));
			position += length + 1;
		}

		if (!jumped)
		{
			offset = position;
		}

		return string.Join('.', labels);
	}

	private static IEnumerable<string> ReadTxt(byte[] data, int start, int length)
	{
		int position = start;
		while (position < start + length)
		{
			int size = data[position];
			yield return Encoding.UTF8.GetString(data, position + 1, size);
			position += size + 1;
		}
	}

	private static IEnumerable<IPAddress> HostsOf(LocalNetwork network)
	{
		uint first = LocalNetwork.ToUInt(network.Network);
		uint count = network.PrefixLength >= 31 ? 0 : (1u << (32 - network.PrefixLength)) - 2;
		for (uint i = 1; i <= count; i++)
		{
			yield return LocalNetwork.ToAddress(first + i);
		}
	}

	private static async Task<bool> IsPortOpenAsync(IPAddress host, int port, CancellationToken ct)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(TimeSpan.FromMilliseconds(700));
		using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
		try
		{
			await socket.ConnectAsync(host, port, timeout.Token);
			return true;
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			return false;
		}
		catch (SocketException e) when (e.SocketErrorCode != SocketError.AccessDenied)
		{
			return false;
		}
	}
}

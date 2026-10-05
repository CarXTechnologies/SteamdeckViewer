using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SteamdeckViewer.Core;

namespace SteamdeckViewer.Tests;

public sealed class DiscoveryTests
{
	[Fact]
	public void QueryAsksPtrForDevkitServiceWithUnicastBit()
	{
		byte[] query = DeckDiscovery.BuildQuery();

		Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4)));
		Assert.Equal("\u000f_steamos-devkit\u0004_tcp\u0005local\0", Encoding.ASCII.GetString(query, 12, query.Length - 16));
		Assert.Equal([0, 12, 0x80, 1], query[^4..]);
	}

	// Ответ в духе avahi: PTR в ответах, SRV/TXT/A в дополнительных записях, имена со сжатием
	[Fact]
	public void ParsesAvahiStyleResponse()
	{
		var packet = new DnsPacket();
		packet.Header(answers: 1, additional: 3);

		int service = packet.Name("_steamos-devkit", "_tcp", "local");
		packet.Rest(12, rdata => rdata.NameWithPointer(["steamdeck"], service), out int instance);

		packet.RecordAt(instance, 33, rdata =>
		{
			rdata.U16(0);
			rdata.U16(0);
			rdata.U16(32000);
			rdata.Name("steamdeck", "local");
		});
		packet.RecordAt(instance, 16, rdata =>
		{
			rdata.Txt("txtvers=1");
			rdata.Txt("login=deck");
			rdata.Txt("settings={}");
		});
		packet.Record(["steamdeck", "local"], 1, rdata => rdata.Bytes(10, 23, 3, 120));

		IReadOnlyList<DiscoveredDeck> decks = DeckDiscovery.ParseResponse(packet.ToArray(), IPAddress.Parse("10.23.3.99"));

		DiscoveredDeck deck = Assert.Single(decks);
		Assert.Equal("steamdeck", deck.Name);
		Assert.Equal("10.23.3.120", deck.Address);
		Assert.Equal(32000, deck.HttpPort);
		Assert.Equal("deck", deck.Login);
	}

	[Fact]
	public void UsesSenderAddressWhenResponseHasOnlyPtr()
	{
		var packet = new DnsPacket();
		packet.Header(answers: 1, additional: 0);
		int service = packet.Name("_steamos-devkit", "_tcp", "local");
		packet.Rest(12, rdata => rdata.NameWithPointer(["my deck"], service), out _);

		DiscoveredDeck deck = Assert.Single(DeckDiscovery.ParseResponse(packet.ToArray(), IPAddress.Parse("10.23.3.120")));
		Assert.Equal("my deck", deck.Name);
		Assert.Equal("10.23.3.120", deck.Address);
		Assert.Equal(DevkitService.DefaultPort, deck.HttpPort);
	}

	[Fact]
	public void IgnoresGarbageAndOtherServices()
	{
		Assert.Empty(DeckDiscovery.ParseResponse([1, 2, 3], IPAddress.Loopback));

		var packet = new DnsPacket();
		packet.Header(answers: 1, additional: 0);
		int service = packet.Name("_googlecast", "_tcp", "local");
		packet.Rest(12, rdata => rdata.NameWithPointer(["tv"], service), out _);
		Assert.Empty(DeckDiscovery.ParseResponse(packet.ToArray(), IPAddress.Loopback));
	}

	[Fact]
	public void NetworkMath()
	{
		var network = new LocalNetwork("Wi-Fi", IPAddress.Parse("10.23.3.84"), 24);
		Assert.Equal("10.23.3.0", network.Network.ToString());
		Assert.Equal("10.23.3.0/24 (Wi-Fi)", network.ToString());
	}

	// Поддельная служба devkit на 127.0.0.1:32000 находится перебором «подсети» 127.0.0.0/30
	[Fact]
	public async Task ScanFindsDevkitServiceOnLoopback()
	{
		using var listener = new HttpListener();
		listener.Prefixes.Add($"http://127.0.0.1:{DevkitService.DefaultPort}/");
		try
		{
			listener.Start();
		}
		catch (HttpListenerException)
		{
			return; // порт 32000 на этом ПК занят
		}

		_ = Task.Run(async () =>
		{
			while (listener.IsListening)
			{
				HttpListenerContext context;
				try
				{
					context = await listener.GetContextAsync();
				}
				catch
				{
					return;
				}

				context.Response.OutputStream.Write("""{"login": "deck"}"""u8);
				context.Response.Close();
			}
		});

		DiscoveryResult result = await DeckDiscovery.ScanAsync([new LocalNetwork("lo", IPAddress.Parse("127.0.0.2"), 30)], null, CancellationToken.None);
		listener.Stop();

		DiscoveredDeck deck = Assert.Single(result.Decks);
		Assert.Equal("127.0.0.1", deck.Address);
		Assert.Equal("deck", deck.Login);
		Assert.False(result.BlockedByFirewall);
	}

	[Fact]
	public async Task MdnsSearchCompletes()
	{
		DiscoveryResult result = await DeckDiscovery.FindAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
		Assert.NotNull(result.Decks);
	}

	[Fact]
	public void RecognizesFirewallBlock()
	{
		Assert.True(NetworkDiagnostics.IsBlockedByFirewall(new HttpRequestException("x", new SocketException((int)SocketError.AccessDenied))));
		Assert.False(NetworkDiagnostics.IsBlockedByFirewall(new SocketException((int)SocketError.ConnectionRefused)));
		Assert.Contains("10.23.0.0/16", NetworkDiagnostics.VpnAdvice("10.23.3.120"));
	}

	private sealed class DnsPacket
	{
		private readonly List<byte> m_data = [];

		public byte[] ToArray() => m_data.ToArray();

		public void Header(int answers, int additional)
		{
			U16(0);
			U16(0x8400);
			U16(0);
			U16(answers);
			U16(0);
			U16(additional);
		}

		// Пишет имя и возвращает его смещение — на него потом ссылаются указатели сжатия
		public int Name(params string[] labels)
		{
			int start = m_data.Count;
			foreach (string label in labels)
			{
				m_data.Add((byte)Encoding.UTF8.GetByteCount(label));
				m_data.AddRange(Encoding.UTF8.GetBytes(label));
			}

			m_data.Add(0);
			return start;
		}

		// Владелец — уже записанное имя, на которое ставится указатель сжатия
		public void RecordAt(int ownerOffset, ushort type, Action<DnsPacket> rdata)
		{
			Pointer(ownerOffset);
			Rest(type, rdata, out _);
		}

		public void Record(string[] owner, ushort type, Action<DnsPacket> rdata)
		{
			Name(owner);
			Rest(type, rdata, out _);
		}

		public void NameWithPointer(string[] labels, int pointer)
		{
			foreach (string label in labels)
			{
				m_data.Add((byte)Encoding.UTF8.GetByteCount(label));
				m_data.AddRange(Encoding.UTF8.GetBytes(label));
			}

			Pointer(pointer);
		}

		public void Txt(string entry)
		{
			m_data.Add((byte)entry.Length);
			m_data.AddRange(Encoding.ASCII.GetBytes(entry));
		}

		public void Bytes(params byte[] bytes) => m_data.AddRange(bytes);

		public void U16(int value)
		{
			m_data.Add((byte)(value >> 8));
			m_data.Add((byte)value);
		}

		private void Pointer(int offset)
		{
			m_data.Add((byte)(0xC0 | (offset >> 8)));
			m_data.Add((byte)offset);
		}

		// Тип, класс, TTL, длина и данные записи после имени владельца
		public void Rest(ushort type, Action<DnsPacket> rdata, out int rdataOffset)
		{
			U16(type);
			U16(0x8001);
			m_data.AddRange([0, 0, 0x11, 0x94]);
			int lengthAt = m_data.Count;
			U16(0);
			rdataOffset = m_data.Count;
			rdata(this);
			int length = m_data.Count - rdataOffset;
			m_data[lengthAt] = (byte)(length >> 8);
			m_data[lengthAt + 1] = (byte)length;
		}
	}
}

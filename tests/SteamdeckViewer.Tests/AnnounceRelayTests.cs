using System.Net;
using System.Net.Sockets;
using System.Text;
using SteamdeckViewer.Core;

namespace SteamdeckViewer.Tests;

public sealed class AnnounceRelayTests
{
	// Слушатель на этом же ПК (как Rider) получает анонс через loopback, даже когда multicast по умолчанию ушёл бы в VPN
	[Fact]
	public async Task RelayedAnnounceReachesLocalListener()
	{
		using var listener = new UdpClient(AddressFamily.InterNetwork);
		listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
		listener.Client.Bind(new IPEndPoint(IPAddress.Any, 34997));
		listener.JoinMulticastGroup(IPAddress.Parse("225.0.0.222"), IPAddress.Loopback);

		using var relay = new UnityAnnounceRelay();
		relay.Start("[IP] 192.168.1.42 [Port] 55000 [Guid] 1 [Debug] 1 [ProjectName] CarX Street", "127.0.0.1");

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		UdpReceiveResult received = await listener.ReceiveAsync(timeout.Token);
		relay.Stop();

		Assert.Equal("[IP] 127.0.0.1 [Port] 55000 [Guid] 1 [Debug] 1 [ProjectName] CarX Street", Encoding.ASCII.GetString(received.Buffer));
	}
}

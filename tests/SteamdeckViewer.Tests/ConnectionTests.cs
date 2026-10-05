using System.Net;
using System.Net.Sockets;
using SteamdeckViewer.Core;

namespace SteamdeckViewer.Tests;

public sealed class ConnectionTests
{
	// Окно показывает подсказку «включите sshd» именно на SocketException с ConnectionRefused
	[Fact]
	public async Task RefusedConnectionSurfacesAsSocketException()
	{
		if (DeckKeys.AvailablePrivateKeys().Count == 0)
		{
			return;
		}

		using var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		int port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();

		var device = new DeckDevice { Host = "127.0.0.1", SshPort = port };
		var e = await Assert.ThrowsAsync<SocketException>(() => DeckConnection.ConnectAsync(device, CancellationToken.None));
		Assert.Equal(SocketError.ConnectionRefused, e.SocketErrorCode);
	}
}

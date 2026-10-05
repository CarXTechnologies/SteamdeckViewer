using System.Net;
using System.Net.Sockets;
using System.Text;
using SteamdeckViewer.Core;

namespace SteamdeckViewer.Tests;

// Поддельная служба devkit на localhost: проверяем, что запросы совпадают с тем, что шлёт клиент Valve
public sealed class DevkitServiceTests
{
	[Fact]
	public async Task RegisterSendsKeyWithMagicPhrase()
	{
		int port = FreePort();
		using var listener = new HttpListener();
		listener.Prefixes.Add($"http://127.0.0.1:{port}/");
		listener.Start();

		string? body = null;
		string? contentType = null;
		Task server = Task.Run(async () =>
		{
			for (int i = 0; i < 2; i++)
			{
				HttpListenerContext context = await listener.GetContextAsync();
				byte[] reply;
				if (context.Request.Url!.AbsolutePath == "/properties.json")
				{
					reply = """{"login": "deck", "settings": "{}"}"""u8.ToArray();
				}
				else
				{
					using var reader = new StreamReader(context.Request.InputStream, Encoding.ASCII);
					body = await reader.ReadToEndAsync();
					contentType = context.Request.ContentType;
					reply = "ok"u8.ToArray();
				}

				context.Response.OutputStream.Write(reply);
				context.Response.Close();
			}
		});

		DevkitProperties? properties = await DevkitService.GetPropertiesAsync("127.0.0.1", port, CancellationToken.None);
		string response = await DevkitService.RegisterAsync("127.0.0.1", port, "ssh-rsa AAAAB3Nza test@host\n", CancellationToken.None);
		await server;

		Assert.Equal("deck", properties?.Login);
		Assert.Equal("ok", response);
		Assert.Equal("ssh-rsa AAAAB3Nza test@host 900b919520e4cf601998a71eec318fec\n", body);
		Assert.StartsWith("text/plain", contentType);
	}

	[Fact]
	public async Task PropertiesReturnNullWhenServiceIsDown()
	{
		Assert.Null(await DevkitService.GetPropertiesAsync("127.0.0.1", FreePort(), CancellationToken.None));
	}

	private static int FreePort()
	{
		using var socket = new TcpListener(IPAddress.Loopback, 0);
		socket.Start();
		return ((IPEndPoint)socket.LocalEndpoint).Port;
	}
}

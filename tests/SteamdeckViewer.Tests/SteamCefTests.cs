using System.Text.Json;
using SteamdeckViewer.Core;

namespace SteamdeckViewer.Tests;

public sealed class SteamCefTests
{
	[Fact]
	public void LaunchOptionsCarryMangoHudEnvironment()
	{
		var profile = new BuildProfile { MangoHudLog = true, MangoHudLogSeconds = 60 };
		string options = MangoHudCapture.SteamLaunchOptions(MangoHudCapture.Environment(profile, "/home/deck/m")!);

		Assert.Equal("env MANGOHUD='1' MANGOHUD_CONFIG='no_display,output_folder=/home/deck/m,autostart_log=1,log_duration=60' %command%", options);
	}

	[Fact]
	public void ScriptPassesOptionsAsJsString()
	{
		const string options = "env A='x\"y' %command%";
		const string call = "SteamClient.Apps.SetAppLaunchOptions(1114150, ";
		string script = SteamCef.LaunchOptionsScript(1114150, options);

		// Строка JSON — корректный строковый литерал JS, кавычки в ней экранированы
		int start = script.IndexOf(call, StringComparison.Ordinal) + call.Length;
		string literal = script[start..script.IndexOf(");", start, StringComparison.Ordinal)];
		Assert.Equal(options, JsonSerializer.Deserialize<string>(literal));
		Assert.DoesNotContain("'", literal);
		Assert.Contains("GetAppDetails?.(1114150)?.strLaunchOptions", script);
	}

	// Ответ /json интерфейса Steam: вкладки Big Picture, оверлеев и SharedJSContext
	[Fact]
	public void FindsSharedJsContext()
	{
		const string targets = """
			[
			  { "title": "Steam Big Picture Mode", "type": "page", "webSocketDebuggerUrl": "ws://localhost:8080/devtools/page/AAA" },
			  { "title": "SharedJSContext", "type": "page", "url": "https://steamloopback.host/routes/", "webSocketDebuggerUrl": "ws://localhost:8080/devtools/page/E1F0" }
			]
			""";

		Assert.Equal("/devtools/page/E1F0", SteamCef.DebuggerPath(targets));
		Assert.Null(SteamCef.DebuggerPath("""[{ "title": "QuickAccess_uid2", "webSocketDebuggerUrl": "ws://localhost:8080/devtools/page/B" }]"""));
	}

	[Fact]
	public void BuildsEvaluateRequest()
	{
		using JsonDocument request = JsonDocument.Parse(SteamCef.EvaluateRequest(7, "1 + 1"));

		Assert.Equal(7, request.RootElement.GetProperty("id").GetInt32());
		Assert.Equal("Runtime.evaluate", request.RootElement.GetProperty("method").GetString());
		Assert.Equal("1 + 1", request.RootElement.GetProperty("params").GetProperty("expression").GetString());
		Assert.True(request.RootElement.GetProperty("params").GetProperty("returnByValue").GetBoolean());
	}

	[Fact]
	public void ParsesEvaluateResponses()
	{
		Assert.Null(SteamCef.ParseResponse("""{ "method": "Runtime.consoleAPICalled", "params": {} }""", 1));
		Assert.Null(SteamCef.ParseResponse("""{ "id": 2, "result": { "result": { "type": "string", "value": "x" } } }""", 1));

		Assert.Equal(("-novid", null), SteamCef.ParseResponse("""{ "id": 1, "result": { "result": { "type": "string", "value": "-novid" } } }""", 1));
		Assert.Equal((null, null), SteamCef.ParseResponse("""{ "id": 1, "result": { "result": { "type": "object", "subtype": "null", "value": null } } }""", 1));
		Assert.Equal((null, "TypeError: SteamClient.Apps.SetAppLaunchOptions is not a function"), SteamCef.ParseResponse("""
			{ "id": 1, "result": { "result": { "type": "object" }, "exceptionDetails": { "text": "Uncaught",
			  "exception": { "description": "TypeError: SteamClient.Apps.SetAppLaunchOptions is not a function" } } } }
			""", 1));
		Assert.Equal((null, "Cannot find context"), SteamCef.ParseResponse("""{ "id": 1, "error": { "code": -32000, "message": "Cannot find context" } }""", 1));
	}
}

public sealed class SteamCefProtocolTests
{
	// Поддельная отладка CEF: /json со списком вкладок и WebSocket SharedJSContext, который сначала шлёт событие,
	// а потом отвечает на Runtime.evaluate значением выражения
	[Fact]
	public async Task EvaluatesThroughDevToolsProtocol()
	{
		var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
		probe.Start();
		int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();

		using var listener = new System.Net.HttpListener();
		listener.Prefixes.Add($"http://localhost:{port}/");
		listener.Start();

		string? received = null;
		Task server = Task.Run(async () =>
		{
			System.Net.HttpListenerContext list = await listener.GetContextAsync();
			byte[] json = System.Text.Encoding.UTF8.GetBytes("""[{ "title": "SharedJSContext", "webSocketDebuggerUrl": "ws://localhost:8080/devtools/page/X1" }]""");
			await list.Response.OutputStream.WriteAsync(json);
			list.Response.Close();

			System.Net.HttpListenerContext ws = await listener.GetContextAsync();
			Assert.Equal("/devtools/page/X1", ws.Request.Url!.AbsolutePath);
			System.Net.WebSockets.WebSocket socket = (await ws.AcceptWebSocketAsync(null)).WebSocket;
			var buffer = new byte[4096];
			System.Net.WebSockets.WebSocketReceiveResult request = await socket.ReceiveAsync(buffer, CancellationToken.None);
			received = System.Text.Encoding.UTF8.GetString(buffer, 0, request.Count);
			foreach (string reply in new[] { """{ "method": "Runtime.executionContextCreated", "params": {} }""", """{ "id": 1, "result": { "result": { "type": "string", "value": "-novid" } } }""" })
			{
				await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(reply), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
			}

			await socket.ReceiveAsync(buffer, CancellationToken.None);
		});

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		string? value = await SteamCef.EvaluateAsync($"localhost:{port}", "1 + 1", timeout.Token);
		await server.WaitAsync(timeout.Token);

		Assert.Equal("-novid", value);
		using JsonDocument request = JsonDocument.Parse(received!);
		Assert.Equal("1 + 1", request.RootElement.GetProperty("params").GetProperty("expression").GetString());
	}
}

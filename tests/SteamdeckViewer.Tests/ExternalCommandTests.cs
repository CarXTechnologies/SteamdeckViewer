using SteamdeckViewer.Core;

namespace SteamdeckViewer.Tests;

public sealed class ExternalCommandTests
{
	[Fact]
	public void ParsesUnityArguments()
	{
		ExternalCommand? command = ExternalCommand.Parse(["--deploy", @"D:\Git\street_reserve\Build\", "--run"]);
		Assert.Equal(new ExternalCommand(@"D:\Git\street_reserve\Build", true), command);

		Assert.False(ExternalCommand.Parse(["--deploy", "D:/Build"])!.Run);
		Assert.Equal(Path.GetFullPath("Build"), ExternalCommand.Parse(["--run", "--deploy", "Build"])!.Folder);

		Assert.Null(ExternalCommand.Parse([]));
		Assert.Null(ExternalCommand.Parse(["--run"]));
		Assert.Null(ExternalCommand.Parse(["--deploy"]));
		Assert.Null(ExternalCommand.Parse(["--deploy", "  "]));
	}

	[Fact]
	public void FindsProfileByBuildFolder()
	{
		var other = new BuildProfile { Name = "Other", LocalFolder = @"D:\Other" };
		var empty = new BuildProfile { Name = "Empty", LocalFolder = "" };
		var street = new BuildProfile { Name = "Street", LocalFolder = @"d:\git\STREET_reserve\Build\" };
		BuildProfile[] profiles = [other, empty, street];

		Assert.Same(street, ExternalCommand.Parse(["--deploy", "D:/Git/street_reserve/Build"])!.FindProfile(profiles));
		Assert.Null(ExternalCommand.Parse(["--deploy", @"D:\Git\street_reserve\Build2"])!.FindProfile(profiles));
	}

	[Fact]
	public void RejectsBrokenPipeMessages()
	{
		Assert.Null(ExternalCommand.TryDeserialize(null));
		Assert.Null(ExternalCommand.TryDeserialize("not json"));
		Assert.Null(ExternalCommand.TryDeserialize("{\"Run\":true}"));
		Assert.Equal(new ExternalCommand(@"D:\B", true), ExternalCommand.TryDeserialize("{\"Folder\":\"D:\\\\B\",\"Run\":true}"));
	}

	// Второй экземпляр программы передаёт команду первому и закрывается
	[Fact]
	public async Task RunningInstanceReceivesCommandsThroughPipe()
	{
		string pipe = "sdv-test-" + Guid.NewGuid().ToString("N");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var received = new List<ExternalCommand>();
		var second = new TaskCompletionSource();
		Task listening = ExternalCommand.ListenAsync(pipe, command =>
		{
			lock (received)
			{
				received.Add(command);
				if (received.Count == 2)
				{
					second.TrySetResult();
				}
			}
		}, cts.Token);

		var first = new ExternalCommand(@"D:\Build", true);
		var next = new ExternalCommand(@"D:\Сборка с пробелом", false);
		Assert.True(await first.SendAsync(pipe, TimeSpan.FromSeconds(5), cts.Token));
		Assert.True(await next.SendAsync(pipe, TimeSpan.FromSeconds(5), cts.Token));
		await second.Task.WaitAsync(cts.Token);

		Assert.Equal([first, next], received);

		await cts.CancelAsync();
		await listening;
	}

	[Fact]
	public async Task SendFailsQuietlyWithoutRunningInstance()
	{
		var command = new ExternalCommand(@"D:\Build", true);
		Assert.False(await command.SendAsync("sdv-test-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(300), CancellationToken.None));
	}
}

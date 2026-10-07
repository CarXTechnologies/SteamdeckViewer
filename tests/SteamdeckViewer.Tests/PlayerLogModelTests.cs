using SteamdeckViewer.Core;

namespace SteamdeckViewer.Tests;

public sealed class PlayerLogModelTests
{
	// Фрагмент настоящего Player.log CarX Street на Deck: нативные строки без пустых строк, затем Debug.LogWarning со стеком
	private static readonly string[] Warning =
	[
		"[Gamescope WSI] Created swapchain for xid: 0x600008 swapchain: 0x2a774070 - imageCount: 3",
		"Could not fetch DPI for display (gamescope 7\"): Couldn't get DPI",
		"UnloadTime: 0.312430 ms",
		"Ignoring invalid [Unity.Entities.UpdateBeforeAttribute] attribute on CarX.Street.ResetDynamicObjectsSystem.",
		"This attribute can only order systems that are members of the same ComponentSystemGroup instance.",
		"UnityEngine.DebugLogHandler:Internal_Log(LogType, LogOption, String, Object)",
		"UnityEngine.Logger:Log(LogType, Object)",
		"UnityEngine.Debug:LogWarning(Object)",
		"Unity.Debug:LogWarning(Object) (at .\\Packages\\com.unity.entities.carxtech\\Unity.Entities\\Stubs\\Unity\\Debug.cs:13)",
		"Unity.Entities.ComponentSystemGroup:SortSystems() (at .\\Packages\\com.unity.entities.carxtech\\Unity.Entities\\ComponentSystemGroup.cs:592)",
		"CarX.Street.GameModes.Base.<>c:<Enter>b__22_0(IEnterHandler) (at D:\\Git\\street_reserve\\Assets\\ControllersContainer.cs:125)",
		""
	];

	private static PlayerLogModel Model(params string[][] blocks)
	{
		var model = new PlayerLogModel(1000);
		foreach (string line in blocks.SelectMany(b => b))
		{
			model.Add(line);
		}

		return model;
	}

	[Theory]
	[InlineData("UnityEngine.DebugLogHandler:Internal_Log(LogType, LogOption, String, Object)", true)]
	[InlineData("Cysharp.Threading.Tasks.AsyncOperationHandleConfiguredSource`1:Continuation(AsyncOperationHandle`1)", true)]
	[InlineData("CarX.Street.GameModes.Base.<EnterAsync>d__14:MoveNext() (at D:\\Git\\GameState.cs:40)", true)]
	[InlineData("UnityEngine.ResourceManagement.AsyncOperationBase`1:UnityEngine.ResourceManagement.IAsyncOperation.InvokeCompletionEvent()", true)]
	[InlineData("  at CarX.Street.Foo.Bar () [0x00000] in <00000000000000000000000000000000>:0 ", true)]
	[InlineData("#0 0x007f1234 in libunity.so", true)]
	[InlineData("Vulkan: Shader requires a compute buffer \"_FogLightDataBuffer\", but none provided.", false)]
	[InlineData("UnloadTime: 0.312430 ms", false)]
	[InlineData("  layer client flags:            0x0", false)]
	[InlineData("[2026-10-07 16:06:47.360] [MANGOHUD] [error] [cpu.cpp:632] Could not find cpu temp sensor location", false)]
	[InlineData("Loading D:\\Git\\file.txt (at start)", false)]
	public void RecognizesStackFrames(string line, bool frame)
	{
		Assert.Equal(frame, PlayerLogModel.IsFrameLine(line));
	}

	[Theory]
	[InlineData("UnityEngine.Debug:LogError(Object)", LogLevel.Error)]
	[InlineData("UnityEngine.Debug:LogErrorFormat(String, Object[])", LogLevel.Error)]
	[InlineData("UnityEngine.Debug:LogException(Exception)", LogLevel.Error)]
	[InlineData("NullReferenceException: Object reference not set to an instance of an object", LogLevel.Error)]
	[InlineData("System.InvalidOperationException: Sequence contains no elements", LogLevel.Error)]
	[InlineData("Rethrow as AggregateException: One or more errors occurred.", LogLevel.Error)]
	[InlineData("Caught fatal signal - signo:11 code:1 errno:0 addr:0x0", LogLevel.Error)]
	[InlineData("[2026-10-07 16:06:47.360] [MANGOHUD] [error] [cpu.cpp:632] Could not find cpu temp sensor location", LogLevel.Error)]
	[InlineData("ERROR: ld.so: object '/home/deck/.local/share/Steam/ubuntu12_32/gameoverlayrenderer.so' from LD_PRELOAD cannot be preloaded (wrong ELF class: ELFCLASS32): ignored.", LogLevel.Warning)]
	[InlineData("UnityEngine.Debug:LogWarningFormat(String, Object[])", LogLevel.Warning)]
	[InlineData("UnityEngine.ResourceManagement.AsyncOperationBase`1:Complete(IList`1, Boolean, Exception, Boolean)", LogLevel.Info)]
	[InlineData("PlayFab API calls will likely fail because you have not set up a HttpWebRequest certificate validation mechanism", LogLevel.Info)]
	[InlineData("UnityEngine.Debug:Log(Object)", LogLevel.Info)]
	public void FindsLevelByLine(string line, LogLevel level)
	{
		Assert.Equal(level, PlayerLogModel.LevelOf(line));
	}

	[Fact]
	public void WarningCoversMessageAndStack()
	{
		PlayerLogModel model = Model(Warning);
		IReadOnlyList<LogLine> lines = model.Lines;

		LogEntry entry = lines[3].Entry!;
		Assert.Equal(LogLevel.Warning, entry.Level);
		Assert.Same(lines[0], entry.First);
		Assert.All(lines.Take(11), l => Assert.Same(entry, l.Entry));
		Assert.Null(lines[11].Entry);
		Assert.Equal(1, model.Count(LogLevel.Warning));
		Assert.False(lines[3].IsFrame);
		Assert.True(lines[5].IsFrame);
	}

	[Fact]
	public void MessageBeforeStackIsLimited()
	{
		string[] native = Enumerable.Range(0, 30).Select(i => $"native line {i}").ToArray();
		PlayerLogModel model = Model(native, ["UnityEngine.Debug:LogError(Object)", ""]);
		IReadOnlyList<LogLine> lines = model.Lines;

		Assert.Equal(LogLevel.Info, lines[19].Level);
		Assert.True(lines[19].StartsEntry);
		Assert.Equal(LogLevel.Error, lines[20].Level);
		Assert.True(lines[20].StartsEntry);
		Assert.Equal(1, model.Count(LogLevel.Error));
		Assert.Equal(20, model.Count(LogLevel.Info));
	}

	[Fact]
	public void StackArrivingLaterRecolorsMessage()
	{
		var model = new PlayerLogModel(1000);
		model.Add("NullReferenceException: Object reference not set to an instance of an object");
		Assert.Equal(LogLevel.Error, model.Lines[0].Level);

		model.Add("Something went wrong");
		Assert.Equal(LogLevel.Info, model.Lines[1].Level);

		model.Add("UnityEngine.Debug:LogError(Object)");
		Assert.Equal(LogLevel.Error, model.Lines[1].Level);
		Assert.Same(model.Lines[0].Entry, model.Lines[2].Entry);
	}

	[Fact]
	public void ToolMessagesStandApart()
	{
		var model = new PlayerLogModel(1000);
		model.Add("Something went wrong");
		model.Add(PlayerLogModel.ToolPrefix + " MangoHud, CarX_Street.csv:\nСредний FPS: 31\n1% low: 22");
		model.Add("UnityEngine.Debug:LogError(Object)");

		Assert.Equal(1, model.Count(LogLevel.Info));
		Assert.Equal(1, model.Count(LogLevel.Error));
		Assert.All(model.Lines.Skip(1).Take(3), l => Assert.True(l.IsTool));
		Assert.Equal(LogLevel.Info, model.Lines[0].Level);
		Assert.True(model.Lines[4].StartsEntry);
		Assert.Equal(LogLevel.Error, model.Lines[4].Level);
	}

	[Fact]
	public void FilterKeepsWholeEntriesWithSeparators()
	{
		PlayerLogModel model = Model(
			["Plain message", "UnityEngine.Debug:Log(Object)", ""],
			Warning,
			["Broken", "UnityEngine.Debug:LogError(Object)", "", PlayerLogModel.ToolPrefix + " заметка", "ERROR: device lost"]);

		Assert.Equal(model.Lines.Count, model.Filter(LogLevel.Info).Count);
		Assert.Equal(["Broken", "UnityEngine.Debug:LogError(Object)", "", "ERROR: device lost"], model.Filter(LogLevel.Error).Select(l => l.Text));

		List<LogLine> warnings = model.Filter(LogLevel.Warning);
		Assert.Equal(Warning.Length + 4, warnings.Count);
		Assert.Equal(Warning[0], warnings[0].Text);
	}

	[Fact]
	public void FindsSearchMatches()
	{
		// Пустой запрос (стёрли поиск) раньше зацикливал перебор и вешал окно
		Assert.Empty(PlayerLogModel.Matches("Vulkan: Shader", ""));
		Assert.Equal([8], PlayerLogModel.Matches("Vulkan: Shader", "shader"));
		Assert.Equal([0, 2], PlayerLogModel.Matches("aaaaa", "aa"));
		Assert.Empty(PlayerLogModel.Matches("", "a"));
	}

	[Fact]
	public void DropsOldestLines()
	{
		var model = new PlayerLogModel(3);
		model.Add("a\nb");
		LogLine a = model.Lines[0];
		model.Add("c\r\nd");

		Assert.Equal(["b", "c", "d"], model.Lines.Select(l => l.Text));
		Assert.True(a.IsRemoved);

		model.Clear();
		Assert.Empty(model.Lines);
	}
}

namespace UIBlazor.Tests.Components.ToolViews;

using Moq;
using UIBlazor.Services.Interfaces;
using UIBlazor.Services.Models;

/// <summary>
/// Reproduces todo.md bug: "при UI рендеринге edit_file не всегда рендерится содержимое
/// изменений, только имя файла... приходит все слишком быстро (меньше секунды) при
/// маленьких изменениях".
///
/// Simulates the REAL pipeline: ToolCall.Function.Arguments grows progressively (the
/// transport re-delivers repaired accumulated JSON per chunk), ToolCallBlock re-renders
/// (its own 100ms throttle — the ONLY throttle left on this branch), DiffView parses the
/// received Edits synchronously. The final assertion: the diff CONTENT must be visible
/// after the stream settles.
/// </summary>
public class EditFileStreamingTests : BunitContext
{
    private readonly Mock<IToolManager> _mockToolManager = new();
    private readonly Mock<IToolCallHandler> _mockToolCallHandler = new();
    private readonly Mock<IContentFilter> _mockContentFilter = new();
    private readonly Mock<ICommonSettingsProvider> _mockCommonSettingsProvider = new();
    private readonly Mock<IProfileManager> _mockProfileManager = new();
    private readonly Mock<UIBlazor.VS.IVsBridge> _mockVsBridge = new();

    public EditFileStreamingTests()
    {
        _mockCommonSettingsProvider.Setup(x => x.Current).Returns(new CommonOptions { ToolTimeoutMs = 120000 });
        _mockProfileManager.Setup(x => x.Current).Returns(new ProfileOptions());
        _mockProfileManager.Setup(x => x.ActiveProfile).Returns(new ConnectionProfile());
        _mockToolManager.Setup(x => x.GetTool(It.IsAny<string>())).Returns((Tool?)null);

        Services.AddSingleton(_mockToolManager.Object);
        Services.AddSingleton(_mockToolCallHandler.Object);
        Services.AddSingleton(_mockContentFilter.Object);
        Services.AddSingleton(_mockCommonSettingsProvider.Object);
        Services.AddSingleton(_mockProfileManager.Object);
        Services.AddSingleton(_mockVsBridge.Object);
        Services.AddSingleton(new UiErrorLogger(new Mock<IJSRuntime>().Object));
        Services.AddRadzenComponents();
        Services.AddSingleton<Radzen.NotificationService>();
        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);

        // Details is real; SubAgentView stubbed (not used here).
        ComponentFactories.AddStub<SubAgentView>();
    }

    private static ToolCall CreateEditFileCall(string args)
    {
        return new ToolCall
        {
            Id = "call-1",
            Function = new ToolCallFunction { Name = "edit_file", Arguments = args }
        };
    }

    /// <summary>
    /// Fast small edit: the whole args stream completes in well under a second. The final
    /// content MUST be rendered.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task FastSmallEditStream_FinalDiffContentIsRendered()
    {
        var toolCall = CreateEditFileCall("""{"filePath":"src/Program.cs"}""");

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, toolCall)
            .Add(p => p.OnApproval, _ => { }));

        // Header is rendered from the start
        Assert.Contains("src/Program.cs", cut.Markup);

        // Simulate progressive streaming: each chunk re-delivers the FULL repaired
        // accumulated JSON (as AfDeltaMapper/AccumulateToolCall produce). Chunks arrive
        // back-to-back (no delay) — the whole stream finishes in < 1 second.
        var chunks = new[]
        {
            "{\"filePath\":\"src/Program.cs\",\"edits\":[{\"OldStr\":\"Console.WriteLine(a\"}",
            "{\"filePath\":\"src/Program.cs\",\"edits\":[{\"OldStr\":\"Console.WriteLine(a);\",\"NewStr\":\"Console.WriteLin\"}]}",
            "{\"filePath\":\"src/Program.cs\",\"edits\":[{\"OldStr\":\"Console.WriteLine(a);\",\"NewStr\":\"Console.WriteLine(EDIT_MARKER_XYZ);\",\"ApproximateLine\":10}]}",
        };

        foreach (var chunk in chunks)
        {
            toolCall.Function.Arguments = chunk;
            cut.Render(parameters => parameters
                .Add(p => p.ToolCall, toolCall)
                .Add(p => p.OnApproval, _ => { }));
        }

        // Stream completed: identical final payload pushed again (stream-completion cascade)
        cut.Render(parameters => parameters
            .Add(p => p.ToolCall, toolCall)
            .Add(p => p.OnApproval, _ => { }));

        // The only delay in the chain is ToolCallBlock's 100ms trailing throttle
        try
        {
            // CharacterChunker splits the new text into per-character spans, so the marker
            // is not contiguous in markup. Assert on the diff structure instead:
            // the modified line with inserted characters must be rendered.
            await cut.WaitForAssertionAsync(
                () => Assert.NotEmpty(cut.FindAll(".inserted-character")),
                TimeSpan.FromSeconds(5));
            Assert.Contains("Console.WriteLine(", cut.Markup); // old side rendered
        }
        catch (Exception)
        {
            throw new Exception($"MARKUP: {cut.Markup}");
        }
    }

    /// <summary>
    /// Same fast-stream scenario but with tiny delays between chunks (still &lt; 1s total),
    /// mirroring a fast network where chunks arrive every ~50-100ms.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task FastSmallEditStream_WithSmallDelays_FinalDiffContentIsRendered()
    {
        var toolCall = CreateEditFileCall("""{"filePath":"src/Program.cs"}""");

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, toolCall)
            .Add(p => p.OnApproval, _ => { }));

        var chunks = new[]
        {
            """{"filePath":"src/Program.cs","edits":[{"OldStr":"old line","NewStr":"new"}]}""",
            """{"filePath":"src/Program.cs","edits":[{"OldStr":"old line","NewStr":"new line"},{"OldStr":"second old","NewStr":"second new FINAL"}]}""",
        };

        foreach (var chunk in chunks)
        {
            await Task.Delay(80, TestContext.Current.CancellationToken);
            toolCall.Function.Arguments = chunk;
            cut.Render(parameters => parameters
                .Add(p => p.ToolCall, toolCall)
                .Add(p => p.OnApproval, _ => { }));
        }

        // Structural signal: the second edit adds a second diff table. The final chunk's
        // render goes through ToolCallBlock's 100ms trailing throttle — WaitForAssertion
        // must cover it (a plain assert after the wait would race the throttle).
        await cut.WaitForAssertionAsync(
            () => Assert.Equal(2, cut.FindAll("table.diff").Count()),
            TimeSpan.FromSeconds(5));
    }
}

using System.Runtime.CompilerServices;

namespace UIBlazor.Tests.Services;

/// <summary>
/// The per-turn request seam of <see cref="ChatService"/>: <c>GetTurnCompletionsAsync</c> rebuilds
/// the request from the SESSION at call time, so the mode-dependent context (system prompt and
/// tool set) is re-derived on every turn instead of being frozen at run start.
/// <para>
/// Migrated from the retired <c>AfAgentLoop</c> mode-switch tests: the loop used to rebuild the
/// prompt and the tool list itself through <c>ModeContextProvider</c>; today the seam derives
/// both from <see cref="ConversationSession.Mode"/>, which makes the compensation unnecessary.
/// </para>
/// </summary>
public class ChatServiceTurnSeamTests
{
    private const string PlanPrompt = "PROMPT:Plan";
    private const string AgentPrompt = "PROMPT:Agent";

    [Fact]
    public async Task TurnSeam_BuildsRequestFromCurrentMode_PromptAndTools()
    {
        var harness = new Harness(AppMode.Plan);

        await ConsumeAsync(harness.Service);

        var turn = Assert.Single(harness.Gateway.Turns);
        Assert.Equal(PlanPrompt, turn.SystemPrompt);
        Assert.Equal(["plan_tool"], turn.ToolNames);
    }

    [Fact]
    public async Task TurnSeam_ModeChangedBetweenTurns_SecondTurnUsesNewPromptAndTools()
    {
        // switch_mode mutates the session while the run is between turns. The retired loop
        // needed ModeContextProvider to notice; now the request is simply built again.
        var harness = new Harness(AppMode.Agent);
        await ConsumeAsync(harness.Service);

        harness.Session.Mode = AppMode.Plan;
        await ConsumeAsync(harness.Service);

        Assert.Equal(2, harness.Gateway.Turns.Count);
        Assert.Equal(AgentPrompt, harness.Gateway.Turns[0].SystemPrompt);
        Assert.Equal(["agent_tool"], harness.Gateway.Turns[0].ToolNames);
        Assert.Equal(PlanPrompt, harness.Gateway.Turns[1].SystemPrompt);
        Assert.Equal(["plan_tool"], harness.Gateway.Turns[1].ToolNames);
    }

    [Fact]
    public async Task TurnSeam_ExplicitToolList_OverridesModeResolution()
    {
        // Sub-agent branches pass their own allowed tools explicitly (null would resolve the
        // tools of the CURRENT MAIN mode - a branch would silently gain tools it may not use).
        var harness = new Harness(AppMode.Agent);

        await ConsumeAsync(harness.Service, tools: []);

        var turn = Assert.Single(harness.Gateway.Turns);
        Assert.Empty(turn.ToolNames);
    }

    [Fact]
    public async Task TurnSeam_TruncatedToolArguments_RepairedInWireHistory()
    {
        // Migrated from the loop's ParseArgs test: the arguments of a tool call can be cut
        // mid-string by an interrupted stream (or by a truncated session file). The repair now
        // lives in the wire mapper (AfMessageMapper.ParseArguments), so the model still sees the
        // partial arguments instead of an empty object.
        var harness = new Harness(AppMode.Agent);
        harness.Session.AddMessage(new VisualChatMessage
        {
            Role = ChatMessageRole.Assistant,
            ToolCalls =
            [
                new ToolCall
                {
                    Id = "call_1",
                    Function = new ToolCallFunction
                    {
                        Name = "read_file",
                        Arguments = """{"filePath":"a.cs","content":"Hello wor"""
                    }
                }
            ]
        });

        await ConsumeAsync(harness.Service);

        var wire = Assert.Single(harness.Gateway.Turns).Wire;
        var assistant = Assert.Single(wire, m => m.Role == ChatMessageRole.Assistant);
        var call = Assert.Single(assistant.ToolCalls!);

        // The wire keeps the raw (broken) string; the repair happens when mapping to the SDK
        // message the model receives.
        var mapped = AfMessageMapper.Map(wire)
            .Single(m => m.Role.Value == ChatMessageRole.Assistant)
            .Contents.OfType<Microsoft.Extensions.AI.FunctionCallContent>()
            .Single();

        Assert.Equal("call_1", mapped.CallId);
        Assert.Equal("read_file", mapped.Name);
        Assert.NotNull(mapped.Arguments);
        Assert.True(mapped.Arguments!.ContainsKey("filePath"),
            "repaired partial arguments must contain filePath");
        Assert.Equal("a.cs",
            Assert.IsType<System.Text.Json.JsonElement>(mapped.Arguments["filePath"]).GetString());
        _ = call;
    }

    private static async Task ConsumeAsync(ChatService service, IReadOnlyList<Tool>? tools = null)
    {
        await foreach (var _ in service.GetTurnCompletionsAsync(
            service.Session, live: null, tools, new CompletionsResult(),
            TestContext.Current.CancellationToken))
        {
        }
    }

    /// <summary>
    /// ChatService wired to a recording gateway: it captures the request the seam built
    /// (system prompt, tool names, wire history) and yields nothing.
    /// </summary>
    private sealed class Harness
    {
        private readonly Mock<ISystemPromptBuilder> _promptBuilder = new();
        private readonly Mock<IToolManager> _toolManager = new();

        public Harness(AppMode mode)
        {
            Session = new ConversationSession { Id = "s", Mode = mode };

            _promptBuilder
                .Setup(b => b.PrepareSystemPromptAsync(It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((AppMode m, CancellationToken _) => m == AppMode.Plan ? PlanPrompt : AgentPrompt);

            _toolManager
                .Setup(t => t.GetEnabledTools(It.IsAny<AppMode>()))
                .Returns((AppMode m) => m == AppMode.Plan
                    ? [MakeTool("plan_tool")]
                    : [MakeTool("agent_tool")]);

            var profileManager = new Mock<IProfileManager>();
            profileManager.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
            {
                Endpoint = "http://localhost:1",
                ApiKey = "key",
                Model = "test-model"
            });

            Service = new ChatService(
                new HttpClient(),
                profileManager.Object,
                _promptBuilder.Object,
                new Mock<ILocalStorageService>().Object,
                new Mock<ILogger<IChatService>>().Object,
                _toolManager.Object,
                new Mock<IContentFilter>().Object,
                new Mock<ICommonSettingsProvider>().Object,
                new Mock<IToolCallHandler>().Object)
            {
                ChatGateway = Gateway
            };

            // The seam builds the request for the CURRENT session object.
            typeof(ChatService)
                .GetProperty(nameof(ChatService.Session))!
                .SetValue(Service, Session);
        }

        public ConversationSession Session { get; }

        public RecordingGateway Gateway { get; } = new();

        public ChatService Service { get; }

        private static Tool MakeTool(string name) => new()
        {
            Name = name,
            NativeTool = new NativeToolDefinition
            {
                Function = new NativeToolFunction
                {
                    Name = name,
                    Description = name,
                    Strict = true,
                    Parameters = new NativeParameters
                    {
                        Type = NativeToolType.Object,
                        Properties = new(),
                        Required = []
                    }
                }
            },
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult())
        };
    }

    /// <summary>Records the per-turn request and yields a single empty delta.</summary>
    private sealed class RecordingGateway : ICompletionsGateway
    {
        public List<RecordedTurn> Turns { get; } = [];

        public async IAsyncEnumerable<ChatDelta> GetCompletionsAsync(
            IReadOnlyList<WireMessage> messages,
            IEnumerable<Tool>? tools,
            ConversationSession targetSession,
            CompletionsResult resultCapture,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Turns.Add(new RecordedTurn(
                messages.FirstOrDefault(m => m.Role == ChatMessageRole.System)?.Content ?? string.Empty,
                [.. (tools ?? []).Select(t => t.Name)],
                messages));

            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed record RecordedTurn(string SystemPrompt, IReadOnlyList<string> ToolNames, IReadOnlyList<WireMessage> Wire);
}

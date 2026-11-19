using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Scripts ONE LLM turn at the WIRE level: the returned <see cref="ChatResponseUpdate"/> stream
/// is pumped through the real <see cref="AfDeltaMapper"/>, so text extraction, tool-call
/// accumulation and token accounting are production code — only the transport is fake.
/// <para>
/// This is the smallest possible substituted surface. A test that needs to observe or influence
/// the run between updates (pause, interject, cancel) simply does it inside the iterator: the
/// service consumes the stream lazily, so a statement between two <c>yield return</c>s runs at
/// exactly that point of the turn.
/// </para>
/// </summary>
internal delegate IAsyncEnumerable<ChatResponseUpdate> WireTurn(
    int index, CancellationToken cancellationToken);

/// <summary>
/// Builders for the updates a <see cref="WireTurn"/> yields.
/// </summary>
internal static class Upd
{
    /// <summary>Assistant text.</summary>
    public static ChatResponseUpdate Text(string text)
        => new(ChatRole.Assistant, [new TextContent(text)]);

    /// <summary>Reasoning ("thinking") text — reported via onStateChange only.</summary>
    public static ChatResponseUpdate Reasoning(string text)
        => new(ChatRole.Assistant, [new TextReasoningContent(text)]);

    /// <summary>One function call. <paramref name="argsJson"/> is a JSON object literal.</summary>
    public static ChatResponseUpdate Call(string id, string name, string argsJson = "{}")
        => new(ChatRole.Assistant, [new FunctionCallContent(id, name, ParseArgs(argsJson))]);

    /// <summary>
    /// Provider usage. <paramref name="totalTokens"/> is the CONTEXT size of this request
    /// (prompt + completion), so successive turns should report a growing number — that is how
    /// a real provider behaves and what makes <c>session.TotalTokens</c> accumulate.
    /// </summary>
    public static ChatResponseUpdate Usage(int totalTokens, int? outputTokens = null)
        => new(ChatRole.Assistant,
        [
            new UsageContent(new UsageDetails
            {
                InputTokenCount = totalTokens - (outputTokens ?? 0),
                OutputTokenCount = outputTokens ?? 0,
                TotalTokenCount = totalTokens
            })
        ]);

    /// <summary>Marks the end of the turn (finish_reason).</summary>
    public static ChatResponseUpdate Stop(string reason = "stop")
        => new(ChatRole.Assistant, []) { FinishReason = ChatFinishReason.Stop };

    private static Dictionary<string, object?> ParseArgs(string json)
    {
        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        return parsed is null
            ? []
            : parsed.ToDictionary(pair => pair.Key, pair => (object?)pair.Value);
    }
}

/// <summary>
/// Scripted <see cref="ICompletionsGateway"/> that replays a <see cref="WireTurn"/> through the
/// REAL <see cref="AfDeltaMapper"/>. Records the request the service built for every turn.
/// </summary>
internal sealed class WireChatGateway(WireTurn turn) : ICompletionsGateway
{
    /// <summary>Turns started (a throwing turn still counts: it was attempted).</summary>
    public int Turns { get; private set; }

    /// <summary>Wire history of every turn, in order — the assertion surface for "what the model saw".</summary>
    public List<IReadOnlyList<WireMessage>> Requests { get; } = [];

    /// <summary>Tool list sent with every turn.</summary>
    public List<IReadOnlyList<Tool>?> RequestTools { get; } = [];

    public IReadOnlyList<WireMessage> Wire(int turnIndex)
    {
        lock (Requests)
            return Requests[turnIndex];
    }

    public async IAsyncEnumerable<ChatDelta> GetCompletionsAsync(
        IReadOnlyList<WireMessage> messages,
        IEnumerable<Tool>? tools,
        ConversationSession targetSession,
        CompletionsResult resultCapture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int index;
        lock (Requests)
        {
            index = Turns++;
            Requests.Add(messages);
            RequestTools.Add(tools as IReadOnlyList<Tool> ?? tools?.ToList());
        }

        await foreach (var delta in AfDeltaMapper.PumpAsync(
            turn(index, cancellationToken), targetSession, resultCapture, cancellationToken))
        {
            yield return delta;
        }
    }
}

/// <summary>
/// The REAL <see cref="ChatService"/> with its transport replaced by a <see cref="WireChatGateway"/>.
/// Everything above the wire is production: request assembly (<c>BuildWire</c>), delta mapping
/// (<c>AfDeltaMapper</c>), delta application and message lifecycle (<c>ProcessStreamAsync</c>),
/// compression (<c>CompressSessionAsync</c>).
/// </summary>
internal sealed class WireScriptedChatService : IDisposable
{
    private WireScriptedChatService(ChatService service, WireChatGateway gateway, WireChatGateway compressionGateway)
    {
        Service = service;
        Gateway = gateway;
        CompressionGateway = compressionGateway;
    }

    public ChatService Service { get; }

    public WireChatGateway Gateway { get; }

    /// <summary>
    /// Gateway behind context compression. Its <see cref="WireChatGateway.Turns"/> counts how many
    /// times the branch gate actually asked for a summary.
    /// </summary>
    public WireChatGateway CompressionGateway { get; }

    /// <summary>Number of compression requests made.</summary>
    public int CompressionCalls => CompressionGateway.Turns;

    /// <summary>Number of LLM turns started.</summary>
    public int Turns => Gateway.Turns;

    /// <summary>Wire history of turn <paramref name="turnIndex"/> (zero-based).</summary>
    public IReadOnlyList<WireMessage> Wire(int turnIndex) => Gateway.Wire(turnIndex);

    /// <summary>
    /// Builds the service.
    /// </summary>
    /// <param name="turn">Per-turn wire script.</param>
    /// <param name="profile">Provider profile (compression threshold / budget live here).</param>
    /// <param name="tools">Tools reported as enabled for every mode.</param>
    /// <param name="compression">Script of a compression request; a canned summary by default.</param>
    /// <param name="systemPrompt">Text returned by the mocked <see cref="ISystemPromptBuilder"/>.</param>
    public static WireScriptedChatService Create(
        WireTurn turn,
        ConnectionProfile profile,
        IReadOnlyList<Tool>? tools = null,
        WireTurn? compression = null,
        string systemPrompt = "sub prompt")
    {
        var chatGateway = new WireChatGateway(turn);

        // Compression is a separate LLM request with no tools. Its own gateway keeps the turn
        // indices of <paramref name="turn"/> intact (a compression request must not consume a
        // scripted turn: the test scripts TURNS, not HTTP calls).
        var compressionGateway = new WireChatGateway(compression ?? ((_, _) => SummaryStream()));

        var profileManager = new Mock<IProfileManager>();
        profileManager.SetupGet(p => p.ActiveProfile).Returns(profile);

        var settings = new Mock<ICommonSettingsProvider>();
        settings.SetupGet(s => s.Current).Returns(new CommonOptions());

        var localStorage = new Mock<ILocalStorageService>();
        localStorage
            .Setup(ls => ls.SetItemAsync(It.IsAny<string>(), It.IsAny<ConversationSession>()))
            .Returns(Task.CompletedTask);
        localStorage.Setup(ls => ls.GetAllKeysAsync()).ReturnsAsync([]);

        var promptBuilder = new Mock<ISystemPromptBuilder>();
        promptBuilder
            .Setup(b => b.PrepareSystemPromptAsync(It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(systemPrompt);

        var toolManager = new Mock<IToolManager>();
        toolManager
            .Setup(t => t.GetEnabledTools(It.IsAny<AppMode>()))
            .Returns(tools ?? []);

        var service = new ChatService(
            new HttpClient(),
            profileManager.Object,
            promptBuilder.Object,
            localStorage.Object,
            NullLogger<IChatService>.Instance,
            toolManager.Object,
            Mock.Of<IContentFilter>(),
            settings.Object,
            Mock.Of<IToolCallHandler>())
        {
            ChatGateway = chatGateway,
            CompressionGateway = compressionGateway,
            AutosaveDelay = TimeSpan.FromSeconds(30)
        };

        return new WireScriptedChatService(service, chatGateway, compressionGateway);
    }

    /// <summary>Marker content of the canned compression summary.</summary>
    public const string SummaryText = "COMPACTED SUMMARY";

    private static async IAsyncEnumerable<ChatResponseUpdate> SummaryStream()
    {
        await Task.Yield();
        yield return Upd.Text(SummaryText);
    }

    public void Dispose() => Service.Dispose();
}

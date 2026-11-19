using System.Runtime.CompilerServices;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Context compression: the summary must be VISIBLE, the history must be rewritten IN PLACE
/// (live streaming messages of the running turn keep their identity) and a short history must
/// not be compressed into noise. Regression for the "compression broke the chat" bug:
/// the summary had no render segments (invisible bubble), the whole list was replaced
/// (the running turn was orphaned → generation froze) and empty turn placeholders filled the
/// head/tail windows.
/// </summary>
public class ChatServiceCompressionTests
{
    private readonly Mock<IProfileManager> _profileManagerMock = new();
    private readonly Mock<ILocalStorageService> _localStorageMock = new();
    private readonly Mock<ICommonSettingsProvider> _commonSettingsProvider = new();

    public ChatServiceCompressionTests()
    {
        _profileManagerMock.SetupGet(p => p.Current).Returns(new ProfileOptions
        {
            Profiles = [],
            ActiveProfileId = "test"
        });
        _profileManagerMock.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            Endpoint = "",
            ApiKey = "test-key",
            ApiKeyHeader = "Authorization",
            Model = "test-model",
            Stream = true,
            TokensToCompress = 1000
        });
        _localStorageMock.Setup(ls => ls.GetAllKeysAsync()).ReturnsAsync([]);
        _localStorageMock
            .Setup(ls => ls.SetItemAsync(It.IsAny<string>(), It.IsAny<ConversationSession>()))
            .Returns(Task.CompletedTask);
        _commonSettingsProvider.Setup(s => s.Current).Returns(new CommonOptions());
    }

    private ChatService CreateService(ICompletionsGateway gateway, params Tool[] tools)
    {
        // The compression path re-arms TotalTokens from the enabled tools of the session's mode,
        // so the toolset must be injectable instead of the empty Moq default.
        var toolManagerMock = new Mock<IToolManager>();
        toolManagerMock
            .Setup(t => t.GetEnabledTools(It.IsAny<AppMode>()))
            .Returns(tools);

        var service = new ChatService(
            new HttpClient(),
            _profileManagerMock.Object,
            Mock.Of<ISystemPromptBuilder>(),
            _localStorageMock.Object,
            new LoggerMock<IChatService>(),
            toolManagerMock.Object,
            Mock.Of<IContentFilter>(),
            _commonSettingsProvider.Object,
            Mock.Of<IToolCallHandler>())
        {
            // Compression is driven by the fake gateway; autosave is irrelevant here.
            CompressionGateway = gateway,
            AutosaveDelay = TimeSpan.FromSeconds(30)
        };
        return service;
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<ChatDelta> stream)
    {
        await foreach (var _ in stream)
        {
        }
    }

    /// <summary>
    /// Appends <paramref name="count"/> alternating user/assistant messages and returns them
    /// (reference-addressed: compression plans by identity).
    /// </summary>
    private static List<VisualChatMessage> Fill(ConversationSession session, int count)
    {
        var messages = new List<VisualChatMessage>();
        for (var i = 0; i < count; i++)
        {
            var message = i % 2 == 0
                ? VisualChatMessage.CreateUser($"u{i}")
                : new VisualChatMessage { Role = ChatMessageRole.Assistant, Content = $"a{i}" };
            messages.Add(message);
            session.AddMessage(message);
        }

        return messages;
    }

    private static List<VisualChatMessage> Visible(ConversationSession session)
        => session.GetMessagesSnapshot().Where(m => !ConversationSession.IsEmptyAssistant(m)).ToList();

    [Fact]
    public async Task Compress_SummaryIsRenderable_AndPlacedBetweenHeadAndTail()
    {
        // Arrange: 8 messages → head 3 verbatim, summary, tail 3 verbatim.
        var gateway = new FakeCompressionGateway("## Goal\n- fix compression");
        using var service = CreateService(gateway);
        var original = Fill(service.Session, 8);

        // Act
        await ConsumeAsync(service.CompressSessionAsync(new CompletionsResult(), TestContext.Current.CancellationToken));

        // Assert: one LLM call, the summary IS the message in the middle of the history and it
        // carries render segments (an assistant message without them renders as an empty bubble).
        Assert.Equal(1, gateway.Calls);
        var messages = service.Session.GetMessagesSnapshot();
        Assert.Equal(7, messages.Count);

        var summary = messages[3];
        Assert.Equal(ChatMessageRole.Assistant, summary.Role);
        Assert.Contains("## Goal", summary.Content);
        Assert.NotEmpty(summary.Segments);

        Assert.Same(original[0], messages[0]);
        Assert.Same(original[1], messages[1]);
        Assert.Same(original[2], messages[2]);
        Assert.Same(original[5], messages[4]);
        Assert.Same(original[6], messages[5]);
        Assert.Same(original[7], messages[6]);
        Assert.DoesNotContain(original[3], messages);
        Assert.DoesNotContain(original[4], messages);
    }

    [Fact]
    public async Task Compress_MidRun_LiveStreamingMessageSurvivesByIdentity()
    {
        // Arrange: the agent loop is streaming into the last message when the gate compresses.
        // That object must stay in the history, otherwise the loop keeps writing into an orphaned
        // message and the UI stops updating ("generation just stopped").
        var gateway = new FakeCompressionGateway("## Goal\n- summary");
        using var service = CreateService(gateway);
        Fill(service.Session, 7);
        var live = VisualChatMessage.CreateStreaming();
        service.Session.AddMessage(live);

        // Act
        await ConsumeAsync(service.CompressSessionAsync(new CompletionsResult(), TestContext.Current.CancellationToken));

        // Assert
        var messages = service.Session.GetMessagesSnapshot();
        Assert.Same(live, messages[^1]);
        Assert.Contains(live, messages);
        Assert.True(live.IsStreaming);
    }

    [Fact]
    public async Task Compress_EmptyTurnPlaceholders_NeverOccupyWindows()
    {
        // Arrange: empty turn-boundary bubbles between the real messages ("4 messages on top plus
        // 2 empty assistant bubbles"). They carry no payload and must not be used as head/tail.
        var gateway = new FakeCompressionGateway("## Goal\n- summary");
        using var service = CreateService(gateway);
        var original = Fill(service.Session, 8);

        var placeholderA = new VisualChatMessage { Role = ChatMessageRole.Assistant };
        var placeholderB = new VisualChatMessage { Role = ChatMessageRole.Assistant };
        service.Session.AddMessage(placeholderA);
        service.Session.AddMessage(placeholderB);

        // Act
        await ConsumeAsync(service.CompressSessionAsync(new CompletionsResult(), TestContext.Current.CancellationToken));

        // Assert: the visible history is [3 head][summary][3 tail] of REAL messages — no empty
        // bubble was promoted into a window.
        var visible = Visible(service.Session);
        Assert.Equal(7, visible.Count);
        Assert.Same(original[0], visible[0]);
        Assert.Same(original[1], visible[1]);
        Assert.Same(original[2], visible[2]);
        Assert.Contains("## Goal", visible[3].Content);
        Assert.Same(original[5], visible[4]);
        Assert.Same(original[6], visible[5]);
        Assert.Same(original[7], visible[6]);
    }

    [Fact]
    public async Task Compress_ShortHistory_NothingToCollapse_SkipsLlmCallAndHistory()
    {
        // Arrange: 4 messages — head and tail already cover everything.
        var gateway = new FakeCompressionGateway("## Goal\n- summary");
        using var service = CreateService(gateway);
        Fill(service.Session, 4);

        // Act
        await ConsumeAsync(service.CompressSessionAsync(new CompletionsResult(), TestContext.Current.CancellationToken));

        // Assert: no LLM call, no history rewrite (an extra summary message used to be appended
        // to the end, which the very next message compressed again).
        Assert.Equal(0, gateway.Calls);
        Assert.Equal(4, service.Session.GetMessageCount());
    }

    [Fact]
    public async Task Compress_EmptySummary_DoesNotDestroyHistory()
    {
        // Arrange: the model returned nothing (interrupted / empty chunk).
        var gateway = new FakeCompressionGateway(string.Empty);
        using var service = CreateService(gateway);
        Fill(service.Session, 8);

        // Act
        await ConsumeAsync(service.CompressSessionAsync(new CompletionsResult(), TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(1, gateway.Calls);
        Assert.Equal(8, service.Session.GetMessageCount());
    }

    [Fact]
    public async Task Compress_LongToolResults_StubbedOnKeptMessages()
    {
        // Arrange: the head message carries a tool result too large for the compressed context.
        var gateway = new FakeCompressionGateway("## Goal\n- summary");
        using var service = CreateService(gateway);

        var toolCall = new ToolCall
        {
            Id = "c1",
            Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" },
            Result = new ToolResult { Content = new string('x', 5000), Name = "read_files", Success = true }
        };
        service.Session.AddMessage(new VisualChatMessage
        {
            Role = ChatMessageRole.Assistant,
            Content = "tool turn",
            ToolCalls = [toolCall]
        });
        Fill(service.Session, 7);

        // Act
        await ConsumeAsync(service.CompressSessionAsync(new CompletionsResult(), TestContext.Current.CancellationToken));

        // Assert: the kept message no longer carries the payload, but the call itself survives.
        Assert.DoesNotContain(new string('x', 100), toolCall.Result!.Content);
        Assert.Contains("was removed after compression", toolCall.Result.Content);
    }

    [Fact]
    public async Task Compress_SessionInterjectDuringCompression_SurvivesAndIsNotConsumed()
    {
        // Arrange: the user sends a message while the summary is being generated. It is appended
        // AFTER the compression snapshot and must survive the in-place collapse, so the NEXT turn
        // still sees it — the turn loop detects it by Revision + message identity (there is no
        // numeric scan baseline anymore).
        VisualChatMessage? interject = null;
        var gateway = new FakeCompressionGateway("## Goal\n- summary", session =>
        {
            interject = VisualChatMessage.CreateUser("interject during compression");
            session.AddMessage(interject);
        });
        using var service = CreateService(gateway);
        Fill(service.Session, 8);
        service.Session.TotalTokens = 5000; // above TokensToCompress (1000)
        var revisionBefore = service.Session.Revision;

        // Act
        await ConsumeAsync(service.CompressSessionAsync(new CompletionsResult(), TestContext.Current.CancellationToken));

        // Assert
        Assert.NotNull(interject);
        var messages = service.Session.GetMessagesSnapshot();
        Assert.Contains(interject, messages);
        Assert.Same(interject, messages[^1]);

        // The interject is NOT consumed by the collapse: it is still part of the next request.
        var wire = service.Session.BuildWire("sys", contentFilter: null, live: null).Messages;
        Assert.Contains(wire, m => m.Role == ChatMessageRole.User
                                   && m.Content == "interject during compression");

        // The compression is one observable history mutation on top of the interject append.
        Assert.True(service.Session.Revision > revisionBefore + 1);

        // The context was re-armed to the compacted size.
        Assert.Equal(8, messages.Count);
    }

    [Fact]
    public async Task Compress_RaisesStartedAndCompleted_WithCollapsedCount()
    {
        // Arrange: the UI has no way to see a compression that runs inside the agent loop
        // (the summary stream is swallowed there), so both edges of the operation must be signalled
        // — a silently frozen chat looks like a bug.
        var gateway = new FakeCompressionGateway("## Goal\n- summary");
        using var service = CreateService(gateway);
        var events = new List<string>();
        CompressionReport? completed = null;
        service.CompressionStarted += session => events.Add($"started:{session.Id}");
        service.CompressionCompleted += report =>
        {
            events.Add("completed");
            completed = report;
        };
        Fill(service.Session, 8);

        // Act
        await ConsumeAsync(service.CompressSessionAsync(new CompletionsResult(), TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(2, events.Count);
        Assert.StartsWith("started:", events[0], StringComparison.Ordinal);
        Assert.Equal("completed", events[1]);
        Assert.NotNull(completed);
        Assert.Same(service.Session, completed.Session);
        Assert.Equal(2, completed.CollapsedCount);
        Assert.Same(service.Session.GetMessagesSnapshot()[3], completed.Summary);
    }

    [Fact]
    public async Task Compress_NothingToCollapse_DoesNotRaiseEvents()
    {
        // Arrange: no LLM call, no history rewrite — the UI must not show "compressed".
        var gateway = new FakeCompressionGateway("## Goal\n- summary");
        using var service = CreateService(gateway);
        var raised = 0;
        service.CompressionStarted += _ => raised++;
        service.CompressionCompleted += _ => raised++;
        Fill(service.Session, 4);

        // Act
        await ConsumeAsync(service.CompressSessionAsync(new CompletionsResult(), TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(0, raised);
    }

    /// <summary>
    /// Re-arming the compression threshold must measure the whole REQUEST, not just the message
    /// text: the tool schemas ride along with every turn (a strict-mode toolset is several
    /// thousand tokens of constant overhead), so leaving them out made the re-armed value
    /// systematically lower than the provider usage it replaces.
    /// </summary>
    [Fact]
    public async Task Compress_ReArmedTokenEstimate_IncludesToolSchemaOverhead()
    {
        // Arrange: identical history compressed twice - once without tools, once with a tool
        // carrying a large schema.
        using var bare = CreateService(new FakeCompressionGateway("## Goal\n- summary"));
        Fill(bare.Session, 8);
        await ConsumeAsync(bare.CompressSessionAsync(new CompletionsResult(), TestContext.Current.CancellationToken));

        using var withSchema = CreateService(
            new FakeCompressionGateway("## Goal\n- summary"),
            BuildToolWithLargeSchema(new string('x', 4000)));
        Fill(withSchema.Session, 8);
        await ConsumeAsync(withSchema.CompressSessionAsync(new CompletionsResult(), TestContext.Current.CancellationToken));

        // Assert: the same history re-arms HIGHER when the request also carries a big schema.
        Assert.True(bare.Session.TotalTokens > 0);
        Assert.True(
            withSchema.Session.TotalTokens > bare.Session.TotalTokens,
            $"expected the schema overhead to be counted: {withSchema.Session.TotalTokens} !> {bare.Session.TotalTokens}");
    }

    /// <summary>
    /// Technical notes must not take part in compression planning: they are not context
    /// (BuildWire drops them), so collapsing one into the summary would edit a line the model
    /// never saw — and the plan windows would be computed over messages that do not exist in the
    /// request.
    /// </summary>
    [Fact]
    public async Task Compress_TechnicalNotes_DoNotOccupyWindowSlots()
    {
        // Arrange: 8 visible messages (just below the planner's head+tail coverage) plus 4 notes.
        // Counting the notes would make the planner collapse the notes themselves.
        var gateway = new FakeCompressionGateway("## Goal\n- summary");
        using var service = CreateService(gateway);
        Fill(service.Session, 8);
        var notes = new List<VisualChatMessage>();
        for (var i = 0; i < 4; i++)
        {
            var note = VisualChatMessage.CreateTechnical($"Retry {i + 1}/10 in 20s — boom");
            notes.Add(note);
            service.Session.AddMessage(note);
        }

        // Act
        await ConsumeAsync(service.CompressSessionAsync(new CompletionsResult(), TestContext.Current.CancellationToken));

        // Assert: compression ran (the visible middle was collapsed) ...
        Assert.Equal(1, gateway.Calls);

        // ... and EVERY note survived. Counting notes in the plan would let them fill the head and
        // tail windows, and the collapsed slice would then consist of diagnostics the model never
        // received (a summary of retry lines is worthless).
        var survivors = service.Session.GetMessagesSnapshot();
        Assert.All(notes, note => Assert.Contains(note, survivors));
        Assert.Contains(survivors, m => m.Content == "## Goal\n- summary");
    }

    /// <summary>Minimal tool whose JSON schema is dominated by <paramref name="description"/>.</summary>
    private static Tool BuildToolWithLargeSchema(string description) => new()
    {
        Name = "probe_tool",
        NativeTool = new NativeToolDefinition
        {
            Function = new NativeToolFunction
            {
                Name = "probe_tool",
                Description = description,
                Parameters = new NativeParameters
                {
                    Type = NativeToolType.Object,
                    Properties = []
                }
            }
        },
        ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Result = "ok" })
    };

    /// <summary>
    /// before the summary is produced (simulates a user interject during the LLM call).
    /// </summary>
    private sealed class FakeCompressionGateway(string summary, Action<ConversationSession>? whileStreaming = null) : ICompletionsGateway
    {
        public int Calls { get; private set; }

        public IReadOnlyList<WireMessage> LastMessages { get; private set; } = [];

        public async IAsyncEnumerable<ChatDelta> GetCompletionsAsync(
            IReadOnlyList<WireMessage> messages,
            IEnumerable<Tool>? tools,
            ConversationSession targetSession,
            CompletionsResult resultCapture,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            LastMessages = messages;
            await Task.Yield();

            if (!string.IsNullOrEmpty(summary))
                yield return new ChatDelta { Content = summary };

            whileStreaming?.Invoke(targetSession);
        }
    }
}

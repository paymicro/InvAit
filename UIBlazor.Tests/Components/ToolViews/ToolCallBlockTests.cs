namespace UIBlazor.Tests.Components.ToolViews;

using UIBlazor.Services.Interfaces;

/// <summary>
/// Tests for <see cref="ToolCallBlock"/>
/// </summary>
public class ToolCallBlockTests : BunitContext
{
    private readonly Mock<IToolManager> _mockToolManager;
    private readonly Mock<IToolCallHandler> _mockToolCallHandler;
    private readonly Mock<IContentFilter> _mockContentFilter;
    private string? _capturedJsonArgs;
    private string? _capturedDiffFilePath;
    private int? _capturedEditCount;
    private (string FilePath, string Content)? _capturedCreateFile;
    private (string Args, string? Answer)? _capturedAskOptions;

    public ToolCallBlockTests()
    {
        _mockToolManager = new Mock<IToolManager>();
        _mockToolCallHandler = new Mock<IToolCallHandler>();
        _mockContentFilter = new Mock<IContentFilter>();

        Services.AddSingleton(_mockToolManager.Object);
        Services.AddSingleton(_mockToolCallHandler.Object);
        Services.AddSingleton(_mockContentFilter.Object);
        Services.AddRadzenComponents();
        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);

        RegisterStubs();
    }

    private void RegisterStubs()
    {
        ComponentFactories.AddStub<JsonArgsView>(parameters => builder =>
        {
            _capturedJsonArgs = parameters.Get(p => p.Args);
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "jsonargs-stub");
            builder.AddContent(2, "json args");
            builder.CloseElement();
        });

        ComponentFactories.AddStub<DiffView>(parameters => builder =>
        {
            _capturedDiffFilePath = parameters.Get(p => p.FilePath);
            _capturedEditCount = parameters.Get(p => p.Edits)?.Length;
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "diffview-stub");
            builder.CloseElement();
        });

        ComponentFactories.AddStub<ToolCreateNewFile>(parameters => builder =>
        {
            _capturedCreateFile = (
                parameters.Get(p => p.FilePath) ?? string.Empty,
                parameters.Get(p => p.Content) ?? string.Empty);
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "createfile-stub");
            builder.CloseElement();
        });

        ComponentFactories.AddStub<ToolAskOptions>(parameters => builder =>
        {
            var args = parameters.Get(p => p.Args) ?? string.Empty;
            var answer = parameters.Get(p => p.Answer);
            var callback = parameters.Get(p => p.OnOptionSelectedCallback);
            _capturedAskOptions = (args, answer);

            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "askoptions-stub");
            if (callback.HasDelegate)
            {
                builder.OpenElement(2, "button");
                builder.AddAttribute(3, "class", "stub-answer");
                builder.AddAttribute(4, "onclick",
                    EventCallback.Factory.Create(this, async () =>
                    {
                        await callback.InvokeAsync("user chosen answer");
                    }));
                builder.AddContent(5, "answer");
                builder.CloseElement();
            }
            builder.CloseElement();
        });

        ComponentFactories.AddStub<DelegateTaskView>(parameters => builder =>
        {
            var args = parameters.Get(p => p.Args);
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "delegatetask-stub");
            builder.AddAttribute(2, "data-args", args);
            builder.CloseElement();
        });

        ComponentFactories.AddStub<SubAgentView>(parameters => builder =>
        {
            var subAgent = parameters.Get(p => p.SubAgent);
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "subagent-stub");
            builder.AddAttribute(2, "data-task", subAgent?.Task);
            builder.CloseElement();
        });

        // Details используется реальный: заголовок блока рендерится через HeaderTemplate,
        // а результат инструмента — через вложенный Details.
    }

    private static Tool CreateTool(string name, string displayName)
    {
        return new Tool
        {
            Name = name,
            DisplayName = displayName,
            NativeTool = new NativeToolDefinition
            {
                Function = new NativeToolFunction
                {
                    Name = name,
                    Description = displayName,
                    Parameters = new NativeParameters { Type = NativeToolType.String, Properties = [] }
                }
            },
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult())
        };
    }

    private static ToolCall CreateReadyCall(string toolName, string args = "")
    {
        return new ToolCall
        {
            Id = "call-1",
            IsReady = true,
            ApprovalStatus = ToolApprovalStatus.Approved,
            Function = new ToolCallFunction { Name = toolName, Arguments = args }
        };
    }

    #region Header Tests

    [Fact]
    public void ShouldRenderHeader_WithDisplayNameFromToolManager()
    {
        // Arrange
        _mockToolManager
            .Setup(x => x.GetTool(BuiltInToolEnum.ReadFiles))
            .Returns(CreateTool(BuiltInToolEnum.ReadFiles, "Read Files Display"));

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.ReadFiles)));

        // Assert
        var header = cut.Find(".tool-call-details .header");
        Assert.Contains(SharedResource.CallingTool, header.TextContent);
        Assert.Contains("Read Files Display", header.TextContent);
    }

    [Fact]
    public void ShouldFallBackToFunctionName_WhenToolNotRegistered()
    {
        // Arrange - GetTool returns null (loose mock default)
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall("some_unknown_tool")));

        // Assert
        Assert.Contains("some_unknown_tool", cut.Find(".tool-call-details .header").TextContent);
    }

    [Fact]
    public void ShouldRenderTokensBadge()
    {
        // Arrange
        var call = CreateReadyCall(BuiltInToolEnum.Grep);
        call.Result = new ToolResult { Name = BuiltInToolEnum.Grep, Content = new string('a', 70) };
        // tokens = 8 + 70/3.5 = 28

        // Act
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call));

        // Assert
        Assert.Contains("~28 tokens", cut.Find(".tool-call-details").TextContent);
    }

    [Fact]
    public void NotReadyCall_ShowsHourglass_AndHidesApprovalFooter()
    {
        // Arrange
        var call = CreateReadyCall(BuiltInToolEnum.Bash);
        call.IsReady = false;

        // Act
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call));

        // Assert
        Assert.NotNull(cut.Find(".tool-call-details .header i.fa-hourglass"));
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".tool-approval-footer"));
    }

    #endregion

    #region Approval Tests

    [Fact]
    public void PendingCall_ShowsApproveAndRejectButtons()
    {
        // Arrange
        var call = CreateReadyCall(BuiltInToolEnum.ReadFiles);
        call.ApprovalStatus = ToolApprovalStatus.Pending;

        // Act
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call));

        // Assert
        Assert.Contains(SharedResource.ApproveRequired, cut.Markup);
        Assert.NotNull(cut.Find(".tool-approve-btn"));
        Assert.NotNull(cut.Find(".tool-reject-btn"));
        Assert.Contains("pending", cut.Find(".tool-call-details").ClassList);
    }

    [Fact]
    public async Task ApproveButton_InvokesOnApproval_WithTrue()
    {
        // Arrange
        var received = default((string SegmentId, bool Approved));
        var call = CreateReadyCall(BuiltInToolEnum.ReadFiles);
        call.ApprovalStatus = ToolApprovalStatus.Pending;

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call)
            .Add(p => p.OnApproval, EventCallback.Factory.Create<(string, bool)>(
                this, args => received = args)));

        // Act
        await cut.InvokeAsync(() => cut.Find(".tool-approve-btn").Click());

        // Assert
        Assert.Equal(("call-1", true), received);
    }

    [Fact]
    public async Task RejectButton_InvokesOnApproval_WithFalse()
    {
        // Arrange
        var received = default((string SegmentId, bool Approved));
        var call = CreateReadyCall(BuiltInToolEnum.ReadFiles);
        call.ApprovalStatus = ToolApprovalStatus.Pending;

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call)
            .Add(p => p.OnApproval, EventCallback.Factory.Create<(string, bool)>(
                this, args => received = args)));

        // Act
        await cut.InvokeAsync(() => cut.Find(".tool-reject-btn").Click());

        // Assert
        Assert.Equal(("call-1", false), received);
    }

    [Fact]
    public void RejectedCall_ShowsRejectedStatus_WithoutButtons()
    {
        // Arrange
        var call = CreateReadyCall(BuiltInToolEnum.ReadFiles);
        call.ApprovalStatus = ToolApprovalStatus.Rejected;

        // Act
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call));

        // Assert
        Assert.Contains("rejected", cut.Find(".tool-call-details").ClassList);
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".tool-approve-btn"));
    }

    #endregion

    #region Arguments Rendering Tests

    [Fact]
    public void GenericTool_RendersJsonArgsView_WithRawArguments()
    {
        // Arrange
        const string args = """{"pattern":"TODO","path":"src"}""";

        // Act
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.Grep, args)));

        // Assert
        Assert.NotNull(cut.Find(".jsonargs-stub"));
        Assert.Equal(args, _capturedJsonArgs);
    }

    [Fact]
    public void EditFilesTool_RendersDiffView_WithParsedEdits()
    {
        // Arrange
        const string args = """
                            {
                              "filePath": "src/App.cs",
                              "edits": [
                                { "oldStr": "a", "newStr": "b" },
                                { "oldStr": "c", "newStr": "d" }
                              ]
                            }
                            """;

        // Act
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.EditFiles, args)));

        // Assert
        Assert.NotNull(cut.Find(".diffview-stub"));
        Assert.Equal("src/App.cs", _capturedDiffFilePath);
        Assert.Equal(2, _capturedEditCount);
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".jsonargs-stub"));
    }

    [Fact]
    public void EditFilesTool_InvalidArguments_RendersEmptyDiffView()
    {
        // Arrange - safety fallback: broken JSON (should not happen - transports deliver
        // repaired valid arguments) must not throw, DiffView gets empty defaults.
        const string args = """{"filePath": """;

        // Act
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.EditFiles, args)));

        // Assert
        Assert.NotNull(cut.Find(".diffview-stub"));
        Assert.Equal(string.Empty, _capturedDiffFilePath);
        Assert.Equal(0, _capturedEditCount);
    }

    [Fact]
    public void CreateFileTool_RendersToolCreateNewFile_WithParsedContent()
    {
        // Arrange
        const string args = """
                            {
                              "filePath": "src/New.cs",
                              "content": "public class New { }"
                            }
                            """;

        // Act
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.CreateFile, args)));

        // Assert
        Assert.NotNull(cut.Find(".createfile-stub"));
        Assert.Equal(("src/New.cs", "public class New { }"), _capturedCreateFile);
    }

    [Fact]
    public void AskUserTool_RendersToolAskOptions_WithArgsAndResultAsAnswer()
    {
        // Arrange
        var call = CreateReadyCall(BasicEnum.AskUser, """{"question":"Q?","options":["A"]}""");
        call.Result = new ToolResult { Name = BasicEnum.AskUser, Content = "chosen" };

        // Act
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call));

        // Assert - ask_user never shows approval footer even when ready
        Assert.NotNull(cut.Find(".askoptions-stub"));
        Assert.Equal((call.Function.Arguments, "chosen"), _capturedAskOptions);
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".tool-approval-footer"));
    }

    [Fact]
    public async Task AskUserAnswer_IsRoutedToInjectedHandler_WithToolCallId()
    {
        // Arrange
        var call = CreateReadyCall(BasicEnum.AskUser, """{"question":"Q?"}""");

        // Act
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call));

        await cut.InvokeAsync(() => cut.Find(".stub-answer").Click());

        // Assert
        _mockToolCallHandler.Verify(
            x => x.HandleAskUserAnswerAsync("call-1", "user chosen answer"),
            Times.Once);
    }

    [Fact]
    public async Task AskUserAnswer_IsRoutedToOverrideHandler_WhenProvided()
    {
        // Arrange
        var overrideHandler = new Mock<IToolCallHandler>();
        var call = CreateReadyCall(BasicEnum.AskUser, """{"question":"Q?"}""");

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call)
            .Add(p => p.ToolCallHandlerOverride, overrideHandler.Object));

        // Act
        await cut.InvokeAsync(() => cut.Find(".stub-answer").Click());

        // Assert
        overrideHandler.Verify(
            x => x.HandleAskUserAnswerAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
        _mockToolCallHandler.Verify(
            x => x.HandleAskUserAnswerAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public void DelegateTaskTool_RendersDelegateTaskView()
    {
        // Arrange
        const string args = """{"task":"Do heavy work"}""";

        // Act
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.DelegateTask, args)));

        // Assert
        var stub = cut.Find(".delegatetask-stub");
        Assert.NotNull(stub);
        Assert.Contains("Do heavy work", stub.GetAttribute("data-args"));
    }

    [Fact]
    public void DelegateTaskWithSubAgent_RendersSubAgentView()
    {
        // Arrange
        var subAgent = new SubAgentMessage { Task = "Sub agent mission", Status = SubAgentStatus.Running };
        var call = CreateReadyCall(BuiltInToolEnum.DelegateTask, """{"task":"x"}""");
        call.SubAgents = [subAgent];

        // Act
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call));

        // Assert
        var stub = cut.Find(".subagent-stub");
        Assert.Equal("Sub agent mission", stub.GetAttribute("data-task"));
    }

    [Fact]
    public async Task SubAgentStateChanged_DoesNotCrashComponent()
    {
        // Arrange
        var subAgent = new SubAgentMessage { Task = "t" };
        var call = CreateReadyCall(BuiltInToolEnum.DelegateTask, """{"task":"x"}""");
        call.SubAgents = [subAgent];

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call));

        // Act - raise state change; component is subscribed and must re-render without error
        await cut.InvokeAsync(() => subAgent.NotifyStateChanged());

        // Assert
        Assert.NotNull(cut.Find(".subagent-stub"));
    }

    [Fact]
    public async Task ToolCallParameterChanged_SubAgentsChangedSubscriptionSurvives()
    {
        // Regression: подписка SubAgentsChanged была только в OnInitialized — при смене
        // параметра ToolCall (новый экземпляр) компонент слушал СТАРЫЙ вызов, и поздние
        // attach'ы (напр. nested batches) не доезжали до UI.
        var firstCall = CreateReadyCall(BuiltInToolEnum.DelegateTask, """{"task":"a"}""");
        var secondCall = CreateReadyCall(BuiltInToolEnum.DelegateTask, """{"task":"b"}""");

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, firstCall));

        // Act - смена параметра ToolCall, затем поздний attach во ВТОРОЙ вызов
        cut.Render(parameters => parameters
            .Add(p => p.ToolCall, secondCall));

        var lateSubAgent = new SubAgentMessage { Task = "late branch", Status = SubAgentStatus.Running };
        secondCall.SubAgents ??= [];
        secondCall.SubAgents.Add(lateSubAgent);
        await cut.InvokeAsync(() => secondCall.NotifySubAgentsChanged());

        // Assert - подписка пережила смену параметра: рендер подхватил новый attach.
        // Рендер структурных изменений проходит через троттлинг 100ms (trailing render) —
        // ждём его через WaitForAssertion.
        cut.WaitForAssertion(
            () =>
            {
                var stubs = cut.FindAll(".subagent-stub");
                Assert.Single(stubs);
                Assert.Equal("late branch", stubs[0].GetAttribute("data-task"));
            },
            TimeSpan.FromSeconds(2));
    }

    #endregion

    #region Streaming Args Tests

    [Fact]
    public void StreamingArgsGrowth_ReRendersBlock_DiffViewReceivesNewEdits()
    {
        // Regression: ToolCall — мутабельная ссылка (args растут внутри неё), параметры
        // формально не меняются → без args-проверки в HasChanges блок не перерендеривался,
        // args не десериализовались и DiffView не получал новый Edits (дифф показывал
        // только финал). Args-мутация входит в HasChanges → блок рендерится (троттлинг
        // 100ms гейтит частоту), DiffView получает обновлённые Edits.
        var call = CreateReadyCall(BuiltInToolEnum.EditFiles, """
            {"filePath":"a.cs","edits":[{"oldStr":"a","newStr":"b"}]}
            """
        );

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call));

        Assert.Equal(1, _capturedEditCount);

        // Стриминг: args растут ВНУТРИ той же ссылки ToolCall (новый edit дописан).
        call.Function.Arguments = """
            {"filePath":"a.cs","edits":[{"oldStr":"a","newStr":"b"},{"oldStr":"c","newStr":"d"}]}
            """
        ;

        // Каскад родителя: параметры reference-equal, но SetParametersAsync вызывается —
        // ShouldRender → HasChanges видит args-рост → рендер (троттлинг 100ms, trailing).
        cut.Render(parameters => parameters
            .Add(p => p.ToolCall, call));

        // Trailing render 100ms: DiffView получает обновлённый Edits (2 секции).
        cut.WaitForAssertion(
            () => Assert.Equal(2, _capturedEditCount),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void StreamingArgsGrowth_ThrottledWithinWindow_StillDeliversFinalArgs()
    {
        // Стриминг: несколько пушей args строго внутри одного throttle-окна (все в
        // пределах 100ms) — ни один не может получить leading-рендер, trailing — единственный
        // шанс показать финал. Финальные args должны доехать до DiffView.
        var call = CreateReadyCall(BuiltInToolEnum.EditFiles, """
            {"filePath":"a.cs","edits":[{"oldStr":"a","newStr":"b"}]}
            """
        );

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call));

        Assert.Equal(1, _capturedEditCount);

        // Три пуша args подряд (каждый добавляет edit) без пауз — все внутри окна.
        for (var i = 2; i <= 4; i++)
        {
            var edits = string.Join(",", Enumerable.Range(0, i)
                .Select(j => $"{{\"oldStr\":\"o{j}\",\"newStr\":\"n{j}\"}}"));
            call.Function.Arguments = $"{{\"filePath\":\"a.cs\",\"edits\":[{edits}]}}";

            cut.Render(parameters => parameters
                .Add(p => p.ToolCall, call));
        }

        // Trailing render доставляет ФИНАЛЬНЫЕ args (4 секции), промежуточные гейтятся.
        cut.WaitForAssertion(
            () => Assert.Equal(4, _capturedEditCount),
            TimeSpan.FromSeconds(2));
    }

    #endregion

    #region Result Tests

    [Fact]
    public void ReadyCallWithResult_RendersResultInDetails()
    {
        // Arrange
        var call = CreateReadyCall(BuiltInToolEnum.ReadFiles, """{"paths":["f.cs"]}""");
        call.Result = new ToolResult
        {
            Name = BuiltInToolEnum.ReadFiles,
            DisplayName = "✅ Read Files Display",
            Content = "file body content"
        };

        // Act
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call));

        // Assert - вложенный Details (результат) внутри внешнего tool-call-details
        var resultDetails = cut.Find(".tool-call-details .custom-details");
        Assert.Contains("✅ Read Files Display", resultDetails.TextContent);
        var pre = resultDetails.QuerySelector("pre");
        Assert.NotNull(pre);
        Assert.Contains("file body content", pre.TextContent);
    }

    [Fact]
    public void ReadyCallWithoutResult_DoesNotRenderResultDetails()
    {
        // Act
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.ReadFiles)));

        // Assert - внутри внешнего Details нет вложенного Details с результатом
        Assert.Throws<ElementNotFoundException>(
            () => cut.Find(".tool-call-details .custom-details"));
    }

    [Fact]
    public async Task ClickHeader_CollapsesWholeBlock_ButContentStaysRendered()
    {
        // Arrange
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.Grep, """{"pattern":"x"}""")));

        Assert.NotNull(cut.Find(".jsonargs-stub"));

        // Act
        await cut.InvokeAsync(() => cut.Find(".tool-call-details .header").Click());

        // Assert - блок свернут (класс снят), но содержимое остается в DOM
        Assert.DoesNotContain("is-expanded", cut.Find(".tool-call-details").ClassList);
        Assert.NotNull(cut.Find(".jsonargs-stub"));
    }

    #endregion

    #region Data Attribute Tests

    [Fact]
    public void ShouldExposeToolNameAndId_InDataAttributes()
    {
        // Act
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.Bash)));

        // Assert
        var block = cut.Find(".tool-call-block");
        Assert.Equal(BuiltInToolEnum.Bash, block.GetAttribute("data-tool"));
        Assert.Equal("call-1", block.GetAttribute("data-toolcall-id"));
    }

    #endregion
}

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
    private readonly Mock<ICommonSettingsProvider> _mockCommonSettingsProvider;
    private readonly Mock<IProfileManager> _mockProfileManager;
    private string? _capturedJsonArgs;
    private string? _capturedDiffFilePath;
    private int? _capturedEditCount;
    private (string FilePath, string Content)? _capturedCreateFile;

    /// <summary>Sentinel for "the parameter was never passed" — distinct from a passed <c>null</c>.</summary>
    private const string NotPassed = "<not passed>";

    private string? _capturedCreateMode;
    private bool? _capturedModeReceived;
    private bool? _capturedArgumentsFinal;
    private bool? _capturedDryRun;
    private DiffEdit[]? _capturedEdits;
    private (string Args, string? Answer)? _capturedAskOptions;
    private string? _capturedBashArgs;

    public ToolCallBlockTests()
    {
        _mockToolManager = new Mock<IToolManager>();
        _mockToolCallHandler = new Mock<IToolCallHandler>();
        _mockContentFilter = new Mock<IContentFilter>();
        _mockCommonSettingsProvider = new Mock<ICommonSettingsProvider>();
        _mockCommonSettingsProvider.Setup(x => x.Current).Returns(new CommonOptions { ToolTimeoutMs = 120000 });
        _mockProfileManager = new Mock<IProfileManager>();
        _mockProfileManager.Setup(x => x.Current).Returns(new ProfileOptions());
        _mockProfileManager.Setup(x => x.ActiveProfile).Returns(new ConnectionProfile());

        Services.AddSingleton(_mockToolManager.Object);
        Services.AddSingleton(_mockToolCallHandler.Object);
        Services.AddSingleton(_mockContentFilter.Object);
        Services.AddSingleton(_mockCommonSettingsProvider.Object);
        Services.AddSingleton(_mockProfileManager.Object);
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
            _capturedEdits = parameters.Get(p => p.Edits);
            // ContainsKey distinguishes "passed as false" from "never passed": a test that only
            // reads the value cannot tell a correct wiring from a deleted one, because the field
            // would keep its default and the assertion would still pass.
            _capturedDryRun = parameters.ContainsKey(nameof(DiffView.DryRun))
                ? parameters.Get(p => p.DryRun)
                : null;
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "diffview-stub");
            builder.CloseElement();
        });

        ComponentFactories.AddStub<ToolCreateNewFile>(parameters => builder =>
        {
            _capturedCreateFile = (
                parameters.Get(p => p.FilePath) ?? string.Empty,
                parameters.Get(p => p.Content) ?? string.Empty);
            _capturedCreateMode = parameters.ContainsKey(nameof(ToolCreateNewFile.Mode))
                ? parameters.Get(p => p.Mode)
                : NotPassed;
            _capturedModeReceived = parameters.ContainsKey(nameof(ToolCreateNewFile.ModeReceived))
                ? parameters.Get(p => p.ModeReceived)
                : null;
            _capturedArgumentsFinal = parameters.ContainsKey(nameof(ToolCreateNewFile.ArgumentsFinal))
                ? parameters.Get(p => p.ArgumentsFinal)
                : null;
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

        ComponentFactories.AddStub<ToolBashCommand>(parameters => builder =>
        {
            _capturedBashArgs = parameters.Get(p => p.Args);
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "bashcommand-stub");
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
    public void BashTool_RendersToolBashCommand_InsteadOfGenericJsonArgs()
    {
        // Arrange - bash несёт description, который печатается отдельной строкой выше команды,
        // поэтому у него свой рендерер (как DiffView у edit_file и ToolCreateNewFile у create_file)
        const string args = """{"command":"git status","description":"Показать состояние"}""";

        // Act
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.Bash, args)));

        // Assert
        Assert.NotNull(cut.Find(".bashcommand-stub"));
        Assert.Equal(args, _capturedBashArgs);
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".jsonargs-stub"));
    }

    [Fact]
    public void EditFileTool_RendersDiffView_WithParsedEdits()
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
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.EditFile, args)));

        // Assert
        Assert.NotNull(cut.Find(".diffview-stub"));
        Assert.Equal("src/App.cs", _capturedDiffFilePath);
        Assert.Equal(2, _capturedEditCount);
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".jsonargs-stub"));
    }

    [Fact]
    public void EditFileTool_InvalidArguments_RendersEmptyDiffView()
    {
        // Arrange - safety fallback: broken JSON (should not happen - transports deliver
        // repaired valid arguments) must not throw, DiffView gets empty defaults.
        const string args = """{"filePath": """;

        // Act
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.EditFile, args)));

        // Assert
        Assert.NotNull(cut.Find(".diffview-stub"));
        Assert.Equal(string.Empty, _capturedDiffFilePath);
        Assert.Equal(0, _capturedEditCount);
    }

    [Theory]
    // Every shape that yields NO renderable edit, and that the host refuses with "Failed to read
    // 'edits'" or "'edits' is empty: nothing to apply". `HasUnusableEdits` used to name only the
    // not-an-array half, so these four produced a diff view with the path and an EMPTY body while
    // the approve/reject buttons were already live — the user was asked to approve an edit they
    // could not see, which is the exact defect this raw-payload fallback exists for.
    [InlineData("""{"filePath":"src/App.cs"}""")]                        // absent: refused
    [InlineData("""{"filePath":"src/App.cs","edits":[]}""")]           // empty: refused
    [InlineData("""{"filePath":"src/App.cs","edits":null}""")]         // null: refused
    [InlineData("""{"filePath":"src/App.cs","edits":[1,2]}""")]        // not objects: refused
    [InlineData("""{"filePath":"src/App.cs","edits":"nope"}""")]     // not an array: refused
    public void EditFileTool_EditsTheHostWouldRefuse_StillShowsTheRawPayload(string args)
    {
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.EditFile, args)));

        // The diff view still renders (it owns the path header), but the payload is readable next
        // to it — an approval gate must never be blank.
        Assert.NotNull(cut.Find(".diffview-stub"));
        Assert.NotNull(cut.Find(".jsonargs-stub"));
        Assert.Equal(args, _capturedJsonArgs);
    }

    [Fact]
    public void EditFileTool_ValidEdits_DoesNotShowTheRawPayload()
    {
        // The positive twin: without it the theory above would pass for a component that ALWAYS
        // renders the raw payload, and the readable diff would be buried under JSON noise.
        const string args = """{"filePath":"src/App.cs","edits":[{"oldStr":"a","newStr":"b"}]}""";

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.EditFile, args)));

        Assert.NotNull(cut.Find(".diffview-stub"));
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".jsonargs-stub"));
    }

    [Fact]
    public void EditFileTool_ArgumentsNormalisedToEmptyObject_StillRendersSomething()
    {
        // `CleanJsonArguments` returns the literal "{}" (length 2) when nothing in the stream could
        // be repaired. The section was gated on `Arguments.Length > 2`, so the WHOLE argument block
        // vanished and the user saw "Calling tool: Edit File" over live Approve/Reject buttons with
        // no path, no diff and no payload — the same blank approval, reached through a different door.
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.EditFile, "{}")));

        Assert.NotNull(cut.Find(".diffview-stub"));
        Assert.NotNull(cut.Find(".jsonargs-stub"));
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
    public void CreateFileTool_PassesModeToTheView()
    {
        // The mode is the whole difference between "created" and "appended to": without this
        // wiring the badge always rendered the overwrite default.
        const string args = """
                            {
                              "filePath": "src/New.cs",
                              "content": "tail",
                              "mode": "append"
                            }
                            """;

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.CreateFile, args)));

        Assert.NotNull(cut.Find(".createfile-stub"));
        Assert.Equal("append", _capturedCreateMode);
        Assert.True(_capturedModeReceived);
    }

    [Fact]
    public void CreateFileTool_ReportsModeAsNotReceived_WhileItIsStillStreaming()
    {
        // A partially streamed payload has no `mode` yet. It resolves to overwrite (the host
        // default), but the badge must stay neutral instead of warning about a destructive mode
        // the call never asked for.
        const string args = """
                            {
                              "filePath": "src/New.cs",
                              "content": "body"
                            }
                            """;

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.CreateFile, args)));

        Assert.NotNull(cut.Find(".createfile-stub"));
        Assert.False(_capturedModeReceived);
    }

    [Fact]
    public void CreateFileTool_ReportsModeAsReceived_WhenItIsExplicitlyNull()
    {
        // `"mode": null` IS the model asking for the default, so the badge may label it.
        const string args = """
                            {
                              "filePath": "src/New.cs",
                              "content": "body",
                              "mode": null
                            }
                            """;

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.CreateFile, args)));

        Assert.True(_capturedModeReceived);
    }

    [Fact]
    public void CreateFileTool_OmitsMode_WhenTheCallOmitsIt()
    {
        // `mode` is required by the schema and arrives as an explicit null, so the view must
        // receive null (not the string "null") and resolve it to overwrite itself.
        const string args = """
                            {
                              "filePath": "src/New.cs",
                              "content": "body",
                              "mode": null
                            }
                            """;

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.CreateFile, args)));

        // Passed as null, NOT forgotten: the sentinel proves the parameter reached the view.
        Assert.Null(_capturedCreateMode);
        Assert.NotEqual(NotPassed, _capturedCreateMode);
    }

    [Fact]
    public void EditFileTool_PassesDryRunToTheView()
    {
        const string args = """
                            {
                              "filePath": "src/App.cs",
                              "edits": [ { "oldStr": "a", "newStr": "b" } ],
                              "dryRun": true
                            }
                            """;

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.EditFile, args)));

        Assert.NotNull(cut.Find(".diffview-stub"));
        Assert.True(_capturedDryRun);
        Assert.NotNull(_capturedDryRun);
    }

    [Fact]
    public void EditFileTool_DryRunNull_IsNotADryRun()
    {
        // `dryRun` is bool? in the schema: null is the model saying "write for real".
        const string args = """
                            {
                              "filePath": "src/App.cs",
                              "edits": [ { "oldStr": "a", "newStr": "b" } ],
                              "dryRun": null
                            }
                            """;

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.EditFile, args)));

        // Passed as false, NOT forgotten — a deleted wiring would leave the field null.
        Assert.False(_capturedDryRun);
        Assert.NotNull(_capturedDryRun);
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    [InlineData("true")]
    public void EditFileTool_DryRunTolerantValues_StillRenderTheBlock(string rawValue)
    {
        // The host reads dryRun with GetBool, which accepts "yes"/1 — so it PERFORMS a dry run.
        // A strict deserialize of that payload throws, and DeserializeAnonymousType answers a throw
        // with ALL defaults: the block would lose its path and its diff exactly when the user is
        // asked to approve the call. Tolerance here is agreement with the host, not laxity.
        var args = $$"""
                     {
                       "filePath": "src/App.cs",
                       "edits": [ { "oldStr": "a", "newStr": "b" } ],
                       "dryRun": {{rawValue}}
                     }
                     """;

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.EditFile, args)));

        Assert.NotNull(cut.Find(".diffview-stub"));
        Assert.Equal("src/App.cs", _capturedDiffFilePath);
        Assert.Equal(1, _capturedEditCount);
        Assert.True(_capturedDryRun);
    }

    [Theory]
    [InlineData("\"12\"")]
    [InlineData("12.7")]
    public void EditFileTool_WrongTypeInsideEdits_StillRendersThePathAndTheDiff(string approximateLine)
    {
        // Every field is read from the raw JSON. A strict deserialize of `"approximateLine": "12"`
        // throws, and `DeserializeAnonymousType` answers a throw with ALL DEFAULTS — so the user was
        // asked to approve an edit with no path and no diff, because the approval buttons live
        // OUTSIDE the args block.
        var args = $$"""
                     {
                       "filePath": "src/App.cs",
                       "edits": [ { "oldStr": "a", "newStr": "b", "approximateLine": {{approximateLine}}, "replaceAll": "yes" } ]
                     }
                     """;

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.EditFile, args)));

        Assert.NotNull(cut.Find(".diffview-stub"));
        Assert.Equal("src/App.cs", _capturedDiffFilePath);
        Assert.Equal(1, _capturedEditCount);
        Assert.Equal("a", _capturedEdits![0].OldStr);
        Assert.Equal("b", _capturedEdits[0].NewStr);
        // `"yes"` is true for the host's GetBool, so the badge must agree with what will execute.
        Assert.True(_capturedEdits[0].ReplaceAll);
    }

    [Fact]
    public void EditFileTool_NullElementInEdits_KeepsTheDenominatorHonest()
    {
        // The `replaceAll 1/2 edits` badge is a ratio over the array. Skipping a null element while
        // the array holds two slots made it read `1/1` — the one number the label exists to show.
        const string args = """
                            {
                              "filePath": "src/App.cs",
                              "edits": [ { "oldStr": "a", "newStr": "b", "replaceAll": true }, null ]
                            }
                            """;

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.EditFile, args)));

        Assert.NotNull(cut.Find(".diffview-stub"));
        Assert.Equal("src/App.cs", _capturedDiffFilePath);
        Assert.Equal(2, _capturedEditCount);
        Assert.True(_capturedEdits![0].ReplaceAll);
    }

    [Fact]
    public void CreateFileTool_ReportsTheArgumentsAsNotFinal_WhileTheStreamIsOpen()
    {
        // The arguments are repaired to valid JSON at every chunk, so a partial `mode` is a legal
        // SHORT value. Without this signal the badge cannot tell "still arriving" from "wrong".
        // The flag comes from the CALL (set while the stream assembles it), NOT from `Result`:
        // `Result` is null through the whole approval wait, which is exactly when a destructive mode
        // has to be visible.
        const string args = """
                            {
                              "filePath": "src/New.cs",
                              "content": "body",
                              "mode": "app"
                            }
                            """;

        var call = CreateReadyCall(BuiltInToolEnum.CreateFile, args);
        call.ArgumentsOpen = true;

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call));

        Assert.NotNull(cut.Find(".createfile-stub"));
        Assert.False(_capturedArgumentsFinal);
    }

    [Fact]
    public void CreateFileTool_ReportsTheArgumentsAsFinal_AtTheApprovalGate()
    {
        // THE case the badge exists for: the stream is over and the user is deciding, but the tool
        // has not run yet, so `Result` is still null. Deriving the flag from `Result` made the mode
        // badge an ellipsis for the whole approval wait and showed the "overwrite" warning only
        // after the file was already truncated.
        const string args = """
                            {
                              "filePath": "src/New.cs",
                              "content": "body",
                              "mode": null
                            }
                            """;

        var call = CreateReadyCall(BuiltInToolEnum.CreateFile, args);
        call.ApprovalStatus = ToolApprovalStatus.Pending;
        Assert.Null(call.Result);

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call));

        Assert.NotNull(cut.Find(".createfile-stub"));
        Assert.True(_capturedArgumentsFinal);
    }

    [Fact]
    public void CreateFileTool_RendersTheRealModeBadge_WhenTheStreamClosesWithNothingElseChanging()
    {
        // REGRESSION GUARD for the render gate, not for the badge.
        //
        // `PrepareToolsForApprovals` finishes a call by doing TWO things: it replaces the arguments
        // with the cleaned string and it clears `ArgumentsOpen`. In the common case neither changes
        // anything else — the approval status is already `Approved` (it is the DEFAULT) and the
        // cleaned payload is character-identical — so a change snapshot built from
        // (status, sub-agent count, has-result, arguments) reported "no change" and the block was
        // never re-rendered. The user kept the "still arriving" ellipsis for the WHOLE execution and
        // saw the `overwrite` warning only once the Result arrived, i.e. after the file was gone.
        //
        // The transition is therefore isolated here: same ToolCall reference, same arguments string
        // instance, same status, no result. Only `ArgumentsOpen` moves.
        const string args = """
                            {
                              "filePath": "src/New.cs",
                              "content": "body",
                              "mode": "overwrite"
                            }
                            """;

        var call = CreateReadyCall(BuiltInToolEnum.CreateFile, args);
        call.ArgumentsOpen = true;

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call));

        Assert.False(_capturedArgumentsFinal);

        call.ArgumentsOpen = false;
        cut.Render(parameters => parameters
            .Add(p => p.ToolCall, call));

        Assert.True(_capturedArgumentsFinal, "the stream-closed transition produced no render");
    }

    [Theory]
    [InlineData("3")]
    [InlineData("\"append\"")]
    public void CreateFileTool_NonStringMode_StillRendersPathAndContent(string rawMode)
    {
        // A non-string mode is a value the host REFUSES with a readable message. The UI must still
        // show the path and the content while it does — a strict deserialize would blank the block.
        var args = $$"""
                     {
                       "filePath": "src/New.cs",
                       "content": "body",
                       "mode": {{rawMode}}
                     }
                     """;

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, CreateReadyCall(BuiltInToolEnum.CreateFile, args)));

        Assert.NotNull(cut.Find(".createfile-stub"));
        Assert.Equal(("src/New.cs", "body"), _capturedCreateFile);
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
        await cut.WaitForAssertionAsync(
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
    public async Task StreamingArgsGrowth_ReRendersBlock_DiffViewReceivesNewEdits()
    {
        // Regression: ToolCall — мутабельная ссылка (args растут внутри неё), параметры
        // формально не меняются → без args-проверки в HasChanges блок не перерендеривался,
        // args не десериализовались и DiffView не получал новый Edits (дифф показывал
        // только финал). Args-мутация входит в HasChanges → блок рендерится (троттлинг
        // 100ms гейтит частоту), DiffView получает обновлённые Edits.
        var call = CreateReadyCall(BuiltInToolEnum.EditFile, """
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
        await cut.WaitForAssertionAsync(
            () => Assert.Equal(2, _capturedEditCount),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task StreamingArgsGrowth_ThrottledWithinWindow_StillDeliversFinalArgs()
    {
        // Стриминг: несколько пушей args строго внутри одного throttle-окна (все в
        // пределах 100ms) — ни один не может получить leading-рендер, trailing — единственный
        // шанс показать финал. Финальные args должны доехать до DiffView.
        var call = CreateReadyCall(BuiltInToolEnum.EditFile, """
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
        await cut.WaitForAssertionAsync(
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

    #region Execution Clock Tests

    [Fact]
    public void RunningCall_RendersCountdownInResultArea_NotInHeader()
    {
        // Arrange: the execution clock was stamped by ToolCallHandler
        var call = CreateReadyCall(BuiltInToolEnum.Bash, """{"command":"echo hi"}""");
        call.ExecutionStartedAtUtc = DateTime.UtcNow.AddSeconds(-2);

        // Act
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call));

        // Assert: the clock belongs to the outcome area, not to the tool name
        var progress = cut.Find(".tool-call-progress");
        Assert.Contains("1:58", progress.TextContent);
        Assert.DoesNotContain("1:58", cut.Find(".tool-call-details .header").TextContent);
    }

    [Fact]
    public void ApprovedButNotStartedCall_RendersNoCountdown()
    {
        // Arrange: Allow-mode batch sets Approved up-front, the tool is still queued
        var call = CreateReadyCall(BuiltInToolEnum.ReadFiles, """{"paths":["f.cs"]}""");

        // Act
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call));

        // Assert
        Assert.Empty(cut.FindAll(".tool-timeout-badge"));
    }

    [Fact]
    public void ResultAppears_ExecutionTimeBadgeMovesToResultBlock()
    {
        // Arrange: the tool ran for 12s and returned a result
        var call = CreateReadyCall(BuiltInToolEnum.ReadFiles, """{"paths":["f.cs"]}""");
        call.ExecutionStartedAtUtc = DateTime.UtcNow.AddSeconds(-12);
        call.ExecutionElapsedMs = 12_000;
        call.Result = new ToolResult { Name = BuiltInToolEnum.ReadFiles, Content = "body", Success = true };

        // Act
        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call));

        // Assert: the badge survives the result — inside the result block, next to the outcome
        var resultHeader = cut.Find(".tool-call-details .custom-details .header");
        Assert.Contains("0:12", resultHeader.TextContent);
        // ...the progress line is gone...
        Assert.Empty(cut.FindAll(".tool-call-progress"));
        // ...and the header still carries only the tool name
        Assert.DoesNotContain("0:12", cut.Find(".tool-call-details .header").TextContent);
    }

    [Fact]
    public void DelegateTaskWithBranches_RendersNoWaitingLine()
    {
        // delegate_task progress is its branch views; the waiting line would duplicate them.
        var call = CreateReadyCall(BuiltInToolEnum.DelegateTask, """{"tasks":[]}""");
        call.SubAgents = [new SubAgentMessage { Task = "branch 1" }];

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call));

        Assert.NotEmpty(cut.FindAll(".subagent-stub"));
        Assert.Empty(cut.FindAll(".tool-call-progress"));
    }

    [Fact]
    public void DelegateTaskWithoutBranches_RendersNoWaitingLine()
    {
        var call = CreateReadyCall(BuiltInToolEnum.DelegateTask, """{"tasks":[]}""");

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call));

        Assert.Empty(cut.FindAll(".tool-call-progress"));
    }

    [Fact]
    public void DelegateTaskResult_ShowsElapsedBadgeInResultBlock()
    {
        var call = CreateReadyCall(BuiltInToolEnum.DelegateTask, """{"tasks":[]}""");
        call.ExecutionStartedAtUtc = DateTime.UtcNow.AddSeconds(-30);
        call.ExecutionElapsedMs = 30_000;
        call.Result = new ToolResult { Name = BuiltInToolEnum.DelegateTask, Content = "report", Success = true };

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call));

        Assert.Contains("0:30", cut.Find(".tool-call-details .custom-details .header").TextContent);
    }

    [Fact]
    public void AskUser_NeitherWaitsInProgressLineNorShowsClock()
    {
        var call = CreateReadyCall(BasicEnum.AskUser, """{"question":"Q?","options":["A"]}""");

        var cut = Render<ToolCallBlock>(parameters => parameters
            .Add(p => p.ToolCall, call));

        // The question itself is the UI; an execution clock would be meaningless here.
        Assert.NotEmpty(cut.FindAll(".askoptions-stub"));
        Assert.Empty(cut.FindAll(".tool-call-progress"));
        Assert.Empty(cut.FindAll(".rz-badge"));
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

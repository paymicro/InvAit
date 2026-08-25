namespace UIBlazor.Tests.Components.Settings;

using AngleSharp.Html.Dom;

/// <summary>
/// Tests for <see cref="MCPTestRun"/>
/// </summary>
public class McpTestRunTests : BunitContext
{
    private readonly Mock<IToolManager> _mockToolManager;
    private readonly List<string> _capturedArguments = [];
    private Tool? _mcpTool;

    public McpTestRunTests()
    {
        _mockToolManager = new Mock<IToolManager>();
        Services.AddSingleton(_mockToolManager.Object);
        Services.AddRadzenComponents();

        // Radzen form components may touch JS interop during lifecycle - silence via Moq
        var mockJsRuntime = new Mock<IJSRuntime>();
        mockJsRuntime
            .Setup(x => x.InvokeAsync<IJSObjectReference>(It.IsAny<string>(), It.IsAny<object[]>()))
            .ReturnsAsync((IJSObjectReference?)null!);
        mockJsRuntime
            .Setup(x => x.InvokeAsync<IJSObjectReference>(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<object[]>()))
            .ReturnsAsync((IJSObjectReference?)null!);
        Services.AddSingleton(mockJsRuntime.Object);

        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);

        // Isolate presentation-only children
        ComponentFactories.AddStub<Details>(parameters => builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "details-stub");
            builder.AddContent(2, parameters.Get(p => p.Text) ?? string.Empty);
            if (parameters.Get(p => p.ChildContent) is { } child)
            {
                builder.AddContent(3, child);
            }
            builder.CloseElement();
        });

        ComponentFactories.AddStub<MarkdownBlock>(parameters => builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "markdown-block-stub");
            builder.AddContent(2, parameters.Get(p => p.Content) ?? string.Empty);
            builder.CloseElement();
        });
    }

    private static McpToolConfig CreateConfig(string schemaJson)
    {
        return new McpToolConfig
        {
            Name = "get_data",
            Description = "Fetches data from the server",
            InputSchema = string.IsNullOrEmpty(schemaJson)
                ? null
                : JsonDocument.Parse(schemaJson).RootElement.Clone()
        };
    }

    /// <summary>
    /// Registers an MCP tool named mcp__{server}__get_data whose serialized arguments are recorded.
    /// </summary>
    private void SetupMcpTool(VsToolResult result)
    {
        _mcpTool = new Tool
        {
            Name = "mcp__testserver__get_data",
            DisplayName = "get_data",
            NativeTool = new NativeToolDefinition
            {
                Function = new NativeToolFunction
                {
                    Name = "mcp__testserver__get_data",
                    Description = "d",
                    Parameters = new NativeParameters { Type = NativeToolType.String, Properties = [] }
                }
            },
            ExecuteAsync = (args, _) =>
            {
                _capturedArguments.Add(args ?? "");
                return Task.FromResult(result);
            }
        };

        _mockToolManager.Setup(x => x.GetMcpTools()).Returns([_mcpTool]);
    }

    #region Rendering Tests

    [Fact]
    public void ShouldRenderDescription_SubmitButton_AndSchemaFields()
    {
        // Arrange
        var tool = CreateConfig("""
                                {
                                  "type":"object",
                                  "properties":{"query":{"type":"string","description":"Search query"}},
                                  "required":["query"]
                                }
                                """);

        // Act
        var cut = Render<MCPTestRun>(parameters => parameters
            .Add(p => p.ServerName, "testserver")
            .Add(p => p.Tool, tool));

        // Assert
        Assert.Contains(SharedResource.Description, cut.Markup);
        Assert.Contains("Fetches data from the server", cut.Find(".markdown-block-stub").TextContent);

        var submitButton = cut.FindComponent<RadzenButton>();
        Assert.Equal("get_data", submitButton.Instance.Text);

        // Schema produced one text box for the "query" property
        var textBox = cut.FindComponent<RadzenTextBox>();
        Assert.Equal("string", textBox.Instance.Placeholder);
    }

    [Fact]
    public void ShouldRenderWithoutErrors_WhenInputSchemaIsNull()
    {
        // Arrange - schema-less tools must render (empty form, no fields)
        var tool = CreateConfig("");

        // Act & Assert
        var exception = Record.Exception(() => Render<MCPTestRun>(parameters => parameters
            .Add(p => p.ServerName, "testserver")
            .Add(p => p.Tool, tool)));
        Assert.Null(exception);
    }

    [Fact]
    public void ResultSection_HiddenInitially()
    {
        // Arrange
        var tool = CreateConfig("""{"type":"object","properties":{}}""");

        // Act
        var cut = Render<MCPTestRun>(parameters => parameters
            .Add(p => p.ServerName, "testserver")
            .Add(p => p.Tool, tool));

        // Assert - only the description markdown block is rendered before run
        Assert.Single(cut.FindAll(".markdown-block-stub"));
    }

    #endregion

    #region Execution Tests

    [Fact]
    public async Task Submit_InvokesMatchingMcpTool_WithFormData()
    {
        // Arrange
        SetupMcpTool(new VsToolResult
        {
            Success = true,
            Result = JsonUtils.Serialize(new MCPToolResult
            {
                Content = [new() { Type = "text", Text = "42" }]
            })
        });

        var tool = CreateConfig("""
                                {
                                  "type":"object",
                                  "properties":{"query":{"type":"string","description":"Search query"}},
                                  "required":["query"]
                                }
                                """);

        var cut = Render<MCPTestRun>(parameters => parameters
            .Add(p => p.ServerName, "testserver")
            .Add(p => p.Tool, tool));

        // Fill the form field produced from the JSON schema
        var textBox = cut.FindComponent<RadzenTextBox>();
        await cut.InvokeAsync(() => textBox.Instance.ValueChanged.InvokeAsync("hello world"));

        // Act - submit the template form
        await cut.InvokeAsync(() => ((IHtmlFormElement)cut.Find("form")).Submit());

        // Assert
        var invocation = Assert.Single(_capturedArguments);
        Assert.Contains("\"query\"", invocation);
        Assert.Contains("hello world", invocation);

        // Result section now visible with the text content
        var blocks = cut.FindAll(".markdown-block-stub");
        Assert.Equal(2, blocks.Count);
        Assert.Contains("42", blocks[1].TextContent);
    }

    [Fact]
    public async Task Submit_WhenToolNotFound_ShowsError()
    {
        // Arrange
        _mockToolManager.Setup(x => x.GetMcpTools()).Returns([]);

        var tool = CreateConfig("""{"type":"object","properties":{}}""");
        var cut = Render<MCPTestRun>(parameters => parameters
            .Add(p => p.ServerName, "other-server")
            .Add(p => p.Tool, tool));

        // Act
        await cut.InvokeAsync(() => ((IHtmlFormElement)cut.Find("form")).Submit());

        // Assert
        Assert.Contains("## Error: Tool is not find", cut.Markup);
    }

    [Fact]
    public async Task Submit_WhenExecutionFails_ShowsErrorResult()
    {
        // Arrange
        SetupMcpTool(VsToolResult.Failed("mcp__testserver__get_data", "boom happened"));

        var tool = CreateConfig("""{"type":"object","properties":{}}""");
        var cut = Render<MCPTestRun>(parameters => parameters
            .Add(p => p.ServerName, "testserver")
            .Add(p => p.Tool, tool));

        // Act
        await cut.InvokeAsync(() => ((IHtmlFormElement)cut.Find("form")).Submit());

        // Assert
        Assert.Contains("## Error: boom happened", cut.Markup);
    }

    [Fact]
    public async Task Submit_WithImageContent_RendersImageSection()
    {
        // Arrange
        SetupMcpTool(new VsToolResult
        {
            Success = true,
            Result = JsonUtils.Serialize(new MCPToolResult
            {
                Content = [new() { Type = "image", Data = "BASE64DATA", MimeType = "image/png" }]
            })
        });

        var tool = CreateConfig("""{"type":"object","properties":{}}""");
        var cut = Render<MCPTestRun>(parameters => parameters
            .Add(p => p.ServerName, "testserver")
            .Add(p => p.Tool, tool));

        // Act
        await cut.InvokeAsync(() => ((IHtmlFormElement)cut.Find("form")).Submit());

        // Assert
        Assert.Contains("## Image", cut.Markup);
        Assert.Contains("BASE64DATA", cut.Markup);
        Assert.Contains("image/png", cut.Markup);
    }

    [Fact]
    public async Task Submit_WithUnparsableResult_ShowsRawResultText()
    {
        // Arrange - result is neither valid MCPToolResult nor failure
        SetupMcpTool(new VsToolResult { Success = true, Result = "plain raw output" });

        var tool = CreateConfig("""{"type":"object","properties":{}}""");
        var cut = Render<MCPTestRun>(parameters => parameters
            .Add(p => p.ServerName, "testserver")
            .Add(p => p.Tool, tool));

        // Act
        await cut.InvokeAsync(() => ((IHtmlFormElement)cut.Find("form")).Submit());

        // Assert - catch branch falls back to raw vsToolResult.Result
        Assert.Contains("plain raw output", cut.Markup);
    }

    #endregion
}

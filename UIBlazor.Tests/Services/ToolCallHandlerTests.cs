namespace UIBlazor.Tests.Services;

/// <summary>
/// Tests for <see cref="ToolCallHandler"/>.
/// </summary>
public partial class ToolCallHandlerTests
{
    private readonly Mock<IToolManager> _toolManagerMock;
    private readonly ToolCallHandler _sut;
    private readonly NativeToolDefinition _nativeTool;

    public ToolCallHandlerTests()
    {
        _toolManagerMock = new Mock<IToolManager>();

        _sut = new ToolCallHandler(_toolManagerMock.Object);
        _nativeTool = new NativeToolDefinition()
        {
            Function = new NativeToolFunction
            {
                Description = "Test tool",
                Name = "test_tool",
                Parameters = new NativeParameters()
                {
                    Type = NativeToolType.String,
                    Properties = []
                }
            }
        };
    }

    [Fact]
    public async Task ProcessToolCallsAsync_EmptyList_DoesNothing()
    {
        // Arrange
        var toolCalls = new List<ToolCall>();

        // Act
        await _sut.ProcessToolCallsAsync(toolCalls, CancellationToken.None);

        // Assert
        _toolManagerMock.Verify(t => t.GetTool(It.IsAny<string>()), Times.Never);
        Assert.Empty(toolCalls);
    }

    [Fact]
    public async Task ProcessToolCallsAsync_ToolNotFound_ReturnsErrorResult()
    {
        // Arrange
        var list = CreateList("unknown_tool", ToolApprovalStatus.Approved);

        _toolManagerMock.Setup(t => t.GetTool("unknown_tool")).Returns((Tool?)null);

        // Act
        await _sut.ProcessToolCallsAsync(list, CancellationToken.None);

        // Assert
        Assert.Single(list);
        Assert.False(list[0].Result.Success);
        Assert.Contains("Tool not found", list[0].Result.Content);
    }

    [Fact]
    public async Task ProcessToolCallsAsync_DeniedTool_ReturnsDeniedResult()
    {
        // Arrange
        var list = CreateList("read_files", ToolApprovalStatus.Rejected);

        var tool = new Tool
        {
            Name = "read_files",
            DisplayName = "Read Files",
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };
        _toolManagerMock.Setup(t => t.GetTool("read_files")).Returns(tool);

        // Act
        await _sut.ProcessToolCallsAsync(list, CancellationToken.None);

        // Assert
        Assert.Single(list);
        Assert.False(list[0].Result.Success);
        Assert.Contains("denied", list[0].Result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessToolCallsAsync_ApprovedTool_ExecutesTool()
    {
        // Arrange
        var list = CreateList("read_files", ToolApprovalStatus.Approved);
        list[0].Function.Arguments = "{ \"path\" : \"test.txt\" }";

        var tool = new Tool
        {
            Name = "read_files",
            DisplayName = "Read Files",
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true, Result = "file content" })
        };

        _toolManagerMock.Setup(t => t.GetTool("read_files")).Returns(tool);

        // Act
        await _sut.ProcessToolCallsAsync(list, CancellationToken.None);

        // Assert
        Assert.Single(list);
        Assert.True(list[0].Result.Success);
        Assert.Contains("file content", list[0].Result.Content);
    }

    [Fact]
    public async Task ProcessToolCallsAsync_McpTool_DeserializesParameters()
    {
        // Arrange
        var list = CreateList("mcp__server__tool_name", ToolApprovalStatus.Approved);
        list[0].Function.Arguments = """
                                                  {
                                                      "param1" : "value1",
                                                      "param2" : "value2"
                                                  }
                                                  """;

        string? capturedArgs = null;

        var tool = new Tool
        {
            Name = "mcp__server__tool_name",
            DisplayName = "MCP Tool",
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = (args, _) =>
            {
                capturedArgs = args;
                return Task.FromResult(new VsToolResult { Success = true });
            }
        };

        _toolManagerMock.Setup(t => t.GetTool("mcp__server__tool_name")).Returns(tool);

        // Act
        await _sut.ProcessToolCallsAsync(list, CancellationToken.None);

        // Assert
        Assert.NotNull(capturedArgs);
        Assert.Equal(list[0].Function.Arguments, capturedArgs);
    }

    [Fact]
    public async Task ProcessToolCallsAsync_AddsToolResult()
    {
        // Arrange
        var list = CreateList("read_files", ToolApprovalStatus.Approved);

        var tool = new Tool
        {
            Name = "read_files",
            DisplayName = "Read Files",
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };

        _toolManagerMock.Setup(t => t.GetTool("read_files")).Returns(tool);

        // Act
        await _sut.ProcessToolCallsAsync(list, CancellationToken.None);

        // Assert
        Assert.Single(list);
        Assert.True(list[0].Result.Success);
    }

    private static List<ToolCall> CreateList(string toolName, ToolApprovalStatus status)
    {
        return [new ToolCall { Id = "1", Function = new ToolCallFunction { Name = toolName }, ApprovalStatus = status }];
    }
}

namespace UIBlazor.Tests.Services;

/// <summary>
/// Tests for <see cref="ToolCallHandler"/>.
/// </summary>
public class ToolCallHandlerTests
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
    public async Task ProcessToolCallsAsync_PendingApproval_WaitsForApproval()
    {
        // Arrange
        var list = CreateList("read_files", ToolApprovalStatus.Pending);

        var tool = new Tool
        {
            Name = "read_files",
            DisplayName = "Read Files",
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };
        _toolManagerMock.Setup(t => t.GetTool("read_files")).Returns(tool);

        // Act - Start processing without approving (won't complete)
        var processTask = _sut.ProcessToolCallsAsync(list, CancellationToken.None);

        // Assert - Task should be waiting for approval
        Assert.False(processTask.IsCompleted);

        // Approve the tool
        await _sut.HandleApprovalAsync(list[0].Id, approved: true);

        // Wait for completion
        await processTask;

        // Assert
        Assert.Equal(ToolApprovalStatus.Approved, list[0].ApprovalStatus);
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
    public async Task HandleApprovalAsync_Approve_SetsApprovedStatus()
    {
        // Arrange - Start a pending approval by starting ProcessToolCallsAsync
        var list = CreateList("read_files", ToolApprovalStatus.Pending);
        var tool = new Tool
        {
            Name = "read_files",
            DisplayName = "Read Files",
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };
        _toolManagerMock.Setup(t => t.GetTool("read_files")).Returns(tool);

        // Act - Start processing and approve
        var processTask = _sut.ProcessToolCallsAsync(list, CancellationToken.None);
        await _sut.HandleApprovalAsync(list[0].Id, approved: true);
        await processTask;

        // Assert
        Assert.Equal(ToolApprovalStatus.Approved, list[0].ApprovalStatus);
        Assert.True(list[0].Result.Success);
    }

    [Fact]
    public async Task HandleApprovalAsync_Reject_SetsRejectedStatus()
    {
        // Arrange
        var list = CreateList("read_files", ToolApprovalStatus.Pending);
        var tool = new Tool
        {
            Name = "read_files",
            DisplayName = "Read Files",
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };
        _toolManagerMock.Setup(t => t.GetTool("read_files")).Returns(tool);

        // Act - Start processing and reject
        var processTask = _sut.ProcessToolCallsAsync(list, CancellationToken.None);
        await _sut.HandleApprovalAsync(list[0].Id, approved: false);
        await processTask;

        // Assert
        Assert.Equal(ToolApprovalStatus.Rejected, list[0].ApprovalStatus);
        Assert.False(list[0].Result.Success);
    }

    [Fact]
    public async Task HandleApprovalAsync_UnknownWaiter_DoesNotThrow()
    {
        // Act & Assert - Should not throw
        await _sut.HandleApprovalAsync("unknown-seg", approved: true);
    }

    [Fact]
    public async Task ProcessToolCallsAsync_Cancellation_ClearsWaitersAndReturns()
    {
        // Arrange
        var list = CreateList("read_files", ToolApprovalStatus.Pending);

        var tool = new Tool
        {
            Name = "read_files",
            DisplayName = "Read Files",
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };
        _toolManagerMock.Setup(t => t.GetTool("read_files")).Returns(tool);

        using var cts = new CancellationTokenSource();

        // Act - Start processing
        var processTask = _sut.ProcessToolCallsAsync(list, cts.Token);

        // Cancel immediately
        cts.Cancel();

        // Assert
        await _sut.HandleApprovalAsync(list[0].Id, approved: false);
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

    [Fact]
    public async Task ProcessToolCallsAsync_OutOfOrderApproval_WorksCorrectly()
    {
        // Arrange - Two pending tools; user approves the second one before the first
        var list = CreateList("tool1", ToolApprovalStatus.Pending);
        list.Add(new ToolCall
        {
            Id = "2",
            Function = new ToolCallFunction { Name = "tool2" },
            ApprovalStatus = ToolApprovalStatus.Pending
        });

        var tool1 = new Tool
        {
            Name = "tool1",
            DisplayName = "Tool 1",
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true, Result = "result1" })
        };
        var tool2 = new Tool
        {
            Name = "tool2",
            DisplayName = "Tool 2",
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true, Result = "result2" })
        };

        _toolManagerMock.Setup(t => t.GetTool("tool1")).Returns(tool1);
        _toolManagerMock.Setup(t => t.GetTool("tool2")).Returns(tool2);

        // Act - Start processing (will block waiting for tool1 approval)
        var processTask = _sut.ProcessToolCallsAsync(list, CancellationToken.None);

        // Approve tool2 FIRST (out of order) - this should not be lost
        await _sut.HandleApprovalAsync(list[0].Id, approved: true);

        // Then approve tool1 - this unblocks the loop
        await _sut.HandleApprovalAsync(list[1].Id, approved: true);

        await processTask;

        // Assert - Both tools should be approved and executed
        Assert.Equal(2, list.Count);
        Assert.Equal(ToolApprovalStatus.Approved, list[0].ApprovalStatus);
        Assert.Equal(ToolApprovalStatus.Approved, list[1].ApprovalStatus);
        Assert.True(list[0].Result.Success);
        Assert.True(list[1].Result.Success);
    }

    private static List<ToolCall> CreateList(string toolName, ToolApprovalStatus status)
    {
        return [new ToolCall { Id = "1", Function = new ToolCallFunction { Name = toolName }, ApprovalStatus = status }];
    }
}

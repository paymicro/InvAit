using Moq;
using Shared.Contracts;
using UIBlazor.Agents;
using UIBlazor.Models;
using UIBlazor.Services;
using UIBlazor.Services.Settings;
using UIBlazor.VS;

namespace UIBlazor.Tests;

public class ToolManagerTests
{
    private readonly IToolManager _toolManager;
    private readonly BuiltInAgent _builtInAgent;
    private readonly Mock<ILocalStorageService> _localStorageMock;

    public ToolManagerTests()
    {
        
        _localStorageMock = new Mock<ILocalStorageService>();

        // Setup default tool
        var tool = new Tool
        {
            Name = "test_tool",
            Description = "Test tool",
            Enabled = true,
            ExecuteAsync = _ => Task.FromResult(new VsToolResult { Success = true, Result = "test result" })
        };
        _builtInAgent = new BuiltInAgent(Mock.Of<IVsBridge>()) { Tools = [tool] };

        _toolManager = new ToolManager(_builtInAgent, _localStorageMock.Object);
    }

    [Fact]
    public void ParseToolBlock_ReadFiles()
    {
        // Arrange
        var content = """
                      <tool_call_begin> functions.read_files
                      C:\Users\user.txt
                      path/to/file2.txt
                      <tool_call_end>
                      """;

        // Act
        var result = _toolManager.ParseToolBlock(content);

        // Assert
        Assert.Single(result);
        var args = result[0].Function.Arguments;
        Assert.Equal("C:\\Users\\user.txt", args["param1"]);
        Assert.Equal("path/to/file2.txt", args["param2"]);
    }

    [Fact]
    public void ParseToolBlock_ApplyDiff()
    {
        // Arrange
        var content = """
                      <tool_call_begin> functions.apply_diff
                      path/to/file.txt
                      :start_line:10
                      <<<<<<< SEARCH
                      old code
                      =======
                      new code {
                          with new lines
                      }
                      >>>>>>> REPLACE
                      <tool_call_end>
                      """;
        var expectedDiff = new DiffReplacement
        {
            StartLine = 10,
            Search = ["old code"],
            Replace = ["new code {", "    with new lines", "}"]
        };

        // Act
        var result = _toolManager.ParseToolBlock(content);

        // Assert
        Assert.Single(result);
        var args = result[0].Function.Arguments;
        Assert.Equal("path/to/file.txt", args["param1"]);
        Assert.Equivalent(expectedDiff, args["diff1"]);
    }

    [Fact]
    public void ParseToolBlock_ApplyDiff_2Blocks()
    {
        // Arrange
        var content = """
                      <tool_call_begin> functions.apply_diff
                      path/to/file.txt
                      :start_line:10
                      <<<<<<< SEARCH
                      old code
                      =======
                      new code {
                          with new lines
                      }
                      >>>>>>> REPLACE

                      :start_line:12
                      <<<<<<< SEARCH
                      public class Test {
                          var bla = "bla"
                      }
                      =======
                      >>>>>>> REPLACE

                      <<<<<<< SEARCH
                      public class Super
                      =======
                      /// <summary>Хех</summary>
                      public class Super
                      >>>>>>> REPLACE
                      <tool_call_end>
                      """;

        // Act
        var result = _toolManager.ParseToolBlock(content);
        var expectedDiff1 = new DiffReplacement
        {
            StartLine = 10,
            Search = ["old code"],
            Replace = ["new code {", "    with new lines", "}"]
        };
        var expectedDiff2 = new DiffReplacement
        {
            StartLine = 12,
            Search = ["public class Test {", "    var bla = \"bla\"", "}"],
            Replace = []
        };
        var expectedDiff3 = new DiffReplacement
        {
            StartLine = -1,
            Search = ["public class Super"],
            Replace = ["/// <summary>Хех</summary>", "public class Super"]
        };

        // Assert
        Assert.Single(result);
        var args = result[0].Function.Arguments;
        Assert.Equal("path/to/file.txt", args["param1"]);
        Assert.Equivalent(expectedDiff1, args["diff1"]);
        Assert.Equivalent(expectedDiff2, args["diff2"]);
        Assert.Equivalent(expectedDiff3, args["diff3"]);
    }

    [Fact]
    public void RegisterAllTools_RegistersToolsFromAgent()
    {
        // Arrange
        var tool1 = new Tool { Name = "tool1", ExecuteAsync = _ => Task.FromResult(new VsToolResult { Success = true, Result = "result1" })};
        var tool2 = new Tool { Name = "tool2", ExecuteAsync = _ => Task.FromResult(new VsToolResult { Success = true, Result = "result2" })};
        _builtInAgent.Tools = [tool1, tool2];

        // Act
        _toolManager.RegisterAllTools();

        // Assert
        Assert.Equal(2, _toolManager.GetAllTools().Count());
        Assert.Contains(_toolManager.GetAllTools(), t => t.Name == "tool1");
        Assert.Contains(_toolManager.GetAllTools(), t => t.Name == "tool2");
    }

    [Fact]
    public void RegisterAllTools_IgnoresToolsWithoutExecuteAsync()
    {
        // Arrange
        var validTool = new Tool { Name = "valid", ExecuteAsync = _ => Task.FromResult(new VsToolResult { Success = true, Result = "result" })};
        var invalidTool = new Tool { Name = "invalid" }; // No ExecuteAsync
        _builtInAgent.Tools = [validTool, invalidTool];

        // Act
        _toolManager.RegisterAllTools();

        // Assert
        Assert.Single(_toolManager.GetAllTools());
        Assert.Equal("valid", _toolManager.GetAllTools().First().Name);
    }

    [Fact]
    public async Task LoadToolSettingsAsync_HandlesExceptionGracefully()
    {
        // Arrange
        _localStorageMock.Setup(ls => ls.GetItemAsync<ToolSettings>(It.IsAny<string>()))
            .ThrowsAsync(new Exception("Storage error"));

        // Act & Assert - should not throw
        await _toolManager.InitializeAsync();
    }

    [Fact]
    public async Task SaveToolSettingsAsync_HandlesExceptionGracefully()
    {
        // Arrange
        _localStorageMock.Setup(ls => ls.SetItemAsync(It.IsAny<string>(), It.IsAny<ToolSettings>()))
            .ThrowsAsync(new Exception("Storage error"));

        // Act & Assert - should not throw
        await _toolManager.SaveToolSettingsAsync();
    }

    [Fact]
    public void GetEnabledTools_ReturnsOnlyEnabledTools()
    {
        // Arrange
        _toolManager.RegisterAllTools();
        _toolManager.GetTool("test_tool")?.Enabled = false;

        // Act
        var enabledTools = _toolManager.GetEnabledTools();

        // Assert
        Assert.Empty(enabledTools);
    }

    [Fact]
    public void GetAllTools_ReturnsAllRegisteredTools()
    {
        // Arrange
        _toolManager.RegisterAllTools();

        // Act
        var allTools = _toolManager.GetAllTools().ToList();

        // Assert
        Assert.Single(allTools);
        Assert.Equal("test_tool", allTools.First().Name);
    }

    [Fact]
    public void GetTool_ReturnsToolByName()
    {
        // Arrange
        _toolManager.RegisterAllTools();

        // Act
        var tool = _toolManager.GetTool("test_tool");
        var nonexistentTool = _toolManager.GetTool("nonexistent");

        // Assert
        Assert.NotNull(tool);
        Assert.Equal("test_tool", tool.Name);
        Assert.Null(nonexistentTool);
    }

    [Fact]
    public void ParseToolBlock_MultipleDifferentTools()
    {
        // Arrange
        var content = """
                      <tool_call_begin> functions.read_files
                      file1.txt
                      <tool_call_end>
                      Some text in between.
                      <tool_call_begin> functions.ls
                      C:\
                      true
                      <tool_call_end>
                      """;

        // Act
        var result = _toolManager.ParseToolBlock(content);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("read_files", result[0].Function.Name);
        Assert.Equal("ls", result[1].Function.Name);
        Assert.Equal("file1.txt", result[0].Function.Arguments["param1"]);
        Assert.Equal("C:\\", result[1].Function.Arguments["param1"]);
        Assert.Equal("true", result[1].Function.Arguments["param2"]);
    }

    [Fact]
    public void GetToolUseSystemInstructions_ReturnsFormattedInstructions()
    {
        // Arrange
        _toolManager.RegisterAllTools();
        var prompt = "Test system prompt";

        // Act
        var instructions = _toolManager.GetToolUseSystemInstructions(AppMode.Agent);

        // Assert
        Assert.Contains("test_tool", instructions);
        Assert.Contains("Test tool", instructions);
        Assert.Contains("function-calling agent", instructions);
    }

    [Fact]
    public void GetToolUseSystemInstructions_ReturnsNoInstructionsWhenNoEnabledTools()
    {
        // Arrange
        _toolManager.RegisterAllTools();
        _toolManager.GetTool("test_tool")?.Enabled = false;

        // Act
        var instructions = _toolManager.GetToolUseSystemInstructions(AppMode.Agent);

        // Assert
        Assert.DoesNotContain("Tool use instructions", instructions);
    }
}

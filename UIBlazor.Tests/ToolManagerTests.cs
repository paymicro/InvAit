using Moq;
using Shared.Contracts;
using UIBlazor.Agents;
using UIBlazor.Models;
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
                      <function name="read_files">
                      C:\Users\user.txt
                      path/to/file2.txt
                      </function>
                      """;

        // Act
        var result = _toolManager.ParseToolBlock(content);

        // Assert
        Assert.Single(result);
        var args = result[0].Function.Arguments;
        Assert.Equal("C:\\Users\\user.txt", ((ReadFileParams)args["file1"]).Name);
        Assert.Equal("path/to/file2.txt", ((ReadFileParams)args["file2"]).Name);
    }

    [Fact]
    public void ParseToolBlock_ReadFiles_WithStartLine()
    {
        // Arrange
        var content = """
                      <function name="read_files">
                      C:\Users\user.txt
                      start_line
                      5
                      </function>
                      """;

        // Act
        var result = _toolManager.ParseToolBlock(content);

        // Assert
        Assert.Single(result);
        var args = result[0].Function.Arguments;
        var file1 = (ReadFileParams)args["file1"];
        Assert.Equal("C:\\Users\\user.txt", file1.Name);
        Assert.Equal(5, file1.StartLine);
        Assert.Equal(-1, file1.LineCount);
    }

    [Fact]
    public void ParseToolBlock_ReadFiles_WithStartLineAndLineCount()
    {
        // Arrange
        var content = """
                      <function name="read_files">
                      C:\Users\user.txt
                      start_line
                      5
                      line_count
                      10
                      </function>
                      """.Replace("\r", "");

        // Act
        var result = _toolManager.ParseToolBlock(content);

        // Assert
        Assert.Single(result);
        var args = result[0].Function.Arguments;
        var file1 = (ReadFileParams)args["file1"];
        Assert.Equal("C:\\Users\\user.txt", file1.Name);
        Assert.Equal(5, file1.StartLine);
        Assert.Equal(10, file1.LineCount);
    }

    [Fact]
    public void ParseToolBlock_ApplyDiff()
    {
        // Arrange
        var content = """
                      <function name="apply_diff">
                      path/to/file.txt
                      :start_line:10
                      <<<<<<< SEARCH
                      old code
                      =======
                      new code {
                          with new lines
                      }
                      >>>>>>> REPLACE
                      </function>
                      """.Replace("\r", "");

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
        Assert.Equivalent("path/to/file.txt", args["param1"]);
        Assert.Equivalent(expectedDiff, args["diff1"]);
    }

    [Fact]
    public void ParseToolBlock_ApplyDiff_2Blocks()
    {
        // Arrange
        var content = """
                      <function name="apply_diff">
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
                      </function>
                      """.Replace("\r", "");

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
        Assert.Equivalent("path/to/file.txt", args["param1"]);
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
                      <function name="read_files">
                      file1.txt
                      </function>
                      Some text in between.
                      <function name="ls">
                      C:\
                      true
                      </function>
                      """.Replace("\r", "");

        // Act
        var result = _toolManager.ParseToolBlock(content);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("read_files", result[0].Function.Name);
        Assert.Equal("ls", result[1].Function.Name);
        Assert.Equal("file1.txt", ((ReadFileParams)result[0].Function.Arguments["file1"]).Name);
        Assert.Equal("C:\\", result[1].Function.Arguments["param1"]);
        Assert.Equal("true", result[1].Function.Arguments["param2"]);
    }

    [Fact]
    public void GetToolUseSystemInstructions_ReturnsFormattedInstructions()
    {
        // Arrange
        _toolManager.RegisterAllTools();

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

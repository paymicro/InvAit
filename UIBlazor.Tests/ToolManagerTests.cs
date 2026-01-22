using Moq;
using Shared.Contracts;
using UIBlazor.Agents;
using UIBlazor.Services;
using UIBlazor.VS;

namespace UIBlazor.Tests;

public class ToolManagerTests
{
    private readonly ToolManager _toolManager;

    public ToolManagerTests()
    {
        var agent = new BuiltInAgent(Mock.Of<IVsBridge>());
        _toolManager = new ToolManager(agent);
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
}

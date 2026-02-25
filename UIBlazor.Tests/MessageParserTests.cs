using Moq;
using Shared.Contracts;
using UIBlazor.Constants;
using UIBlazor.Models;
using UIBlazor.Services;
using UIBlazor.Services.Settings;

namespace UIBlazor.Tests;

public class MessageParserTests
{
    private readonly MessageParser _parser;
    private readonly Mock<IToolManager> _toolManagerMock;

    public MessageParserTests()
    {
        _toolManagerMock = new Mock<IToolManager>();
        _parser = new MessageParser(_toolManagerMock.Object);
    }

    [Fact]
    public void UpdateSegments_MarkdownOnly()
    {
        // Arrange
        var message = new VisualChatMessage { Role = ChatMessageRole.Assistant };
        var delta = "Hello, world!";

        // Act
        _parser.UpdateSegments(delta, message);

        // Assert
        Assert.Single(message.Segments);
        Assert.Equal(SegmentType.Markdown, message.Segments[0].Type);
        Assert.Equal("Hello, world!", message.Segments[0].CurrentLine.ToString());
    }

    [Fact]
    public void UpdateSegments_SingleToolCall()
    {
        // Arrange
        var message = new VisualChatMessage { Role = ChatMessageRole.Assistant };
        var delta = """
                    <function name="read_files">
                    file1.txt
                    </function>
                    """;

        // Act
        _parser.UpdateSegments(delta, message);

        // Assert
        Assert.Single(message.Segments);
        var segment = message.Segments[0];
        Assert.Equal(SegmentType.Tool, segment.Type);
        Assert.Equal("read_files", segment.ToolName);
        Assert.Equal("file1.txt", segment.Lines[0].TrimEnd());
        Assert.True(segment.IsClosed);
    }

    [Fact]
    public void UpdateSegments_MixedContent()
    {
        // Arrange
        var message = new VisualChatMessage { Role = ChatMessageRole.Assistant };
        var delta = """
                    Thinking...
                    <function name="ls">
                    dir/
                    </function>
                    Done.
                    """;

        // Act
        _parser.UpdateSegments(delta, message);

        // Assert
        Assert.Equal(3, message.Segments.Count);

        Assert.Equal(SegmentType.Markdown, message.Segments[0].Type);
        Assert.Equal("Thinking...\r\n", message.Segments[0].CurrentLine.ToString());
        Assert.True(message.Segments[0].IsClosed);

        Assert.Equal(SegmentType.Tool, message.Segments[1].Type);
        Assert.Equal("ls", message.Segments[1].ToolName);
        Assert.Equal("dir/", message.Segments[1].Lines[0].TrimEnd());
        Assert.True(message.Segments[1].IsClosed);

        Assert.Equal(SegmentType.Markdown, message.Segments[2].Type);
        Assert.Equal("Done.", message.Segments[2].CurrentLine.ToString().Trim());
    }

    [Fact]
    public void UpdateSegments_IncrementalUpdate()
    {
        // Arrange
        var message = new VisualChatMessage { Role = ChatMessageRole.Assistant };

        // Act
        _parser.UpdateSegments("<function name=\"read_files\">", message);
        _parser.UpdateSegments("\nfile.txt\n", message);
        _parser.UpdateSegments("</function>", message);

        // Assert
        Assert.Single(message.Segments);
        var segment = message.Segments[0];
        Assert.Equal(SegmentType.Tool, segment.Type);
        Assert.Equal("read_files", segment.ToolName);
        Assert.Equal("file.txt", segment.Lines[0]);
        Assert.True(segment.IsClosed);
    }

    [Fact]
    public void Parse_ReadFiles()
    {
        // Arrange
        var lines = new List<string> { "C:\\Users\\user.txt", "path/to/file2.txt" };

        // Act
        var args = _parser.Parse(BuiltInToolEnum.ReadFiles, lines);

        // Assert
        Assert.Equal("C:\\Users\\user.txt", ((ReadFileParams)args["file1"]).Name);
        Assert.Equal("path/to/file2.txt", ((ReadFileParams)args["file2"]).Name);
    }

    [Fact]
    public void Parse_ReadFiles_WithStartLine()
    {
        // Arrange
        var lines = new List<string> { "C:\\Users\\user.txt", "start_line", "5" };

        // Act
        var args = _parser.Parse(BuiltInToolEnum.ReadFiles, lines);

        // Assert
        var file1 = (ReadFileParams)args["file1"];
        Assert.Equal("C:\\Users\\user.txt", file1.Name);
        Assert.Equal(5, file1.StartLine);
        Assert.Equal(-1, file1.LineCount);
    }

    [Fact]
    public void Parse_ReadFiles_WithStartLineAndLineCount()
    {
        // Arrange
        var lines = new List<string> { "C:\\Users\\user.txt", "start_line", "5", "line_count", "10" };

        // Act
        var args = _parser.Parse(BuiltInToolEnum.ReadFiles, lines);

        // Assert
        var file1 = (ReadFileParams)args["file1"];
        Assert.Equal("C:\\Users\\user.txt", file1.Name);
        Assert.Equal(5, file1.StartLine);
        Assert.Equal(10, file1.LineCount);
    }

    [Fact]
    public void Parse_ApplyDiff()
    {
        // Arrange
        var lines = new List<string>
        {
            "path/to/file.txt",
            ":start_line:10",
            "<<<<<<< SEARCH",
            "old code",
            "=======",
            "new code {",
            "    with new lines",
            "}",
            ">>>>>>> REPLACE"
        };

        var expectedDiff = new DiffReplacement
        {
            StartLine = 10,
            Search = ["old code"],
            Replace = ["new code {", "    with new lines", "}"]
        };

        // Act
        var args = _parser.Parse(BuiltInToolEnum.ApplyDiff, lines);

        // Assert
        Assert.Equal("path/to/file.txt", args["param1"]);
        Assert.Equivalent(expectedDiff, args["diff1"]);
    }

    [Fact]
    public void Parse_ApplyDiff_2Blocks()
    {
        // Arrange
        var lines = new List<string>
        {
            "path/to/file.txt",
            ":start_line:10",
            "<<<<<<< SEARCH",
            "old code",
            "=======",
            "new code {",
            "    with new lines",
            "}",
            ">>>>>>> REPLACE",
            "",
            ":start_line:12",
            "<<<<<<< SEARCH",
            "public class Test {",
            "    var bla = \"bla\"",
            "}",
            "=======",
            ">>>>>>> REPLACE",
            "",
            "<<<<<<< SEARCH",
            "public class Super",
            "=======",
            "/// <summary>Хех</summary>",
            "public class Super",
            ">>>>>>> REPLACE"
        };

        // Act
        var args = _parser.Parse(BuiltInToolEnum.ApplyDiff, lines);

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
        Assert.Equal("path/to/file.txt", args["param1"]);
        Assert.Equivalent(expectedDiff1, args["diff1"]);
        Assert.Equivalent(expectedDiff2, args["diff2"]);
        Assert.Equivalent(expectedDiff3, args["diff3"]);
    }
}

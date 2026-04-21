using AngleSharp.Common;
using Moq;
using Shared.Contracts;
using UIBlazor.Constants;
using UIBlazor.Models;
using UIBlazor.Services;
using UIBlazor.Services.Settings;
using UIBlazor.Utils;

namespace UIBlazor.Tests.Services;

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
    public void UpdateSegments_IncrementalUpdate2()
    {
        // Arrange
        var message = new VisualChatMessage { Role = ChatMessageRole.Assistant };

        // Act
        _parser.UpdateSegments("<function name=\"mcp__invaitmcp__get_service_info\">\nserviceName", message);
        _parser.UpdateSegments(" : \"wow", message);
        _parser.UpdateSegments("-service\"\n", message);
        _parser.UpdateSegments("num :", message);
        _parser.UpdateSegments(" 123\n", message);
        _parser.UpdateSegments("</function>", message);
        _parser.UpdateSegments("\n", message);
        _parser.UpdateSegments("\n", message);

        // Assert
        Assert.Equal(2, message.Segments.Count);
        var segment = message.Segments[0];
        Assert.Equal(SegmentType.Tool, segment.Type);
        Assert.Equal("mcp__invaitmcp__get_service_info", segment.ToolName);
        Assert.Equal("serviceName : \"wow-service\"", segment.Lines[0]);
        Assert.Equal("num : 123", segment.Lines[1]);
        Assert.True(segment.IsClosed);
    }

    [Fact]
    public void UpdateSegments_IncrementalUpdate3()
    {
        // Arrange
        var message = new VisualChatMessage { Role = ChatMessageRole.Assistant };

        // Act
        _parser.UpdateSegments("<function name = \"read_files\">", message);
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
    public void UpdateSegments_ApplyDiff_Incremental_ShouldNotDuplicate()
    {
        // Arrange
        var message = new VisualChatMessage { Role = ChatMessageRole.Assistant };
        _toolManagerMock.Setup(x => x.GetApprovalModeByToolName("apply_diff"))
            .Returns(ToolApprovalMode.Allow);

        // Act
        _parser.UpdateSegments("<function name=\"apply_diff\">", message);
        _parser.UpdateSegments("\npath/to/file.txt\n", message);
        _parser.UpdateSegments("<<<<<<< SEARCH :10:\n", message);
        _parser.UpdateSegments("old code\n", message);
        _parser.UpdateSegments("=======\n", message);
        _parser.UpdateSegments("new code\n", message);
        _parser.UpdateSegments(">>>>>>> REPLACE\n", message);
        _parser.UpdateSegments("</function>", message);

        // Assert
        Assert.Single(message.Segments);
        var segment = message.Segments[0];
        Assert.Equal(SegmentType.Tool, segment.Type);
        Assert.Equal("apply_diff", segment.ToolName);
        Assert.True(segment.IsClosed);

        // Проверяем что строки НЕ дублируются
        var lines = segment.Lines.Select(l => l.TrimEnd()).ToList();
        Assert.DoesNotContain(lines, l => lines.Count(x => x == l) > 1); // Нет дубликатов
    }

    [Fact]
    public void UpdateSegments_ApplyDiff_MultiLineInOneChunk()
    {
        // Arrange
        var message = new VisualChatMessage { Role = ChatMessageRole.Assistant };
        _toolManagerMock.Setup(x => x.GetApprovalModeByToolName("apply_diff"))
            .Returns(ToolApprovalMode.Allow);

        // Act - несколько строк в одном чанке (проблемный случай!)
        _parser.UpdateSegments("<function name=\"apply_diff\">\nline1", message);
        _parser.UpdateSegments("\nline2\nline3\n", message);
        _parser.UpdateSegments("</function>", message);
        // 3 строки сразу
        _parser.UpdateSegments("", message);


        // Assert
        var segment = message.Segments[0];
        var lines = segment.Lines.Select(l => l.TrimEnd()).ToList();
        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void Parse_ReadFiles()
    {
        // Arrange
        var lines = new List<string> { "C:\\Users\\user.txt", "path/to/file2.txt" };

        // Act
        var args = MessageParser.Parse(BuiltInToolEnum.ReadFiles, lines);

        // Assert
        Assert.Equal("C:\\Users\\user.txt", ((ReadFileParams)args["file1"]).Name);
        Assert.Equal("path/to/file2.txt", ((ReadFileParams)args["file2"]).Name);
    }

    [Fact]
    public void Parse_ReadFiles_WithStartLine()
    {
        // Arrange
        var lines = new List<string> { "C:\\Users\\user.txt [L5]" };

        // Act
        var args = MessageParser.Parse(BuiltInToolEnum.ReadFiles, lines);

        // Assert
        var file1 = (ReadFileParams)args["file1"];
        Assert.Equal("C:\\Users\\user.txt", file1.Name);
        Assert.Equal(5, file1.StartLine);
        Assert.Equal(-1, file1.LineCount);
    }

    [Fact]
    public void UpdateSegments_FunctuionReadThreeFiles_WithParams_IncrementalUpdate()
    {
        // Arrange
        var lines = new List<string> {
            "C:\\user\\project\\file.cs",
            "C:\\user\\project\\file2.cs [L550]",
            "C:\\user\\project\\file3.cs [L100:C50]"
        };

        // Act
        var args = MessageParser.Parse(BuiltInToolEnum.ReadFiles, lines);

        // Assert
        var file1 = (ReadFileParams)args["file1"];
        Assert.Equal("C:\\user\\project\\file.cs", file1.Name);
        Assert.Equal(-1, file1.StartLine);
        Assert.Equal(-1, file1.LineCount);

        var file2 = (ReadFileParams)args["file2"];
        Assert.Equal("C:\\user\\project\\file2.cs", file2.Name);
        Assert.Equal(550, file2.StartLine);
        Assert.Equal(-1, file2.LineCount);

        var file3 = (ReadFileParams)args["file3"];
        Assert.Equal("C:\\user\\project\\file3.cs", file3.Name);
        Assert.Equal(100, file3.StartLine);
        Assert.Equal(50, file3.LineCount);
    }

    [Fact]
    public void Parse_ReadFiles_WithStartLineAndLineCount()
    {
        // Arrange
        var lines = new List<string> { "C:\\Users\\user.txt [L5:C10]" };

        // Act
        var args = MessageParser.Parse(BuiltInToolEnum.ReadFiles, lines);

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
            "<<<<<<< SEARCH :10:",
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
        var args = MessageParser.Parse(BuiltInToolEnum.ApplyDiff, lines);

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
            "<<<<<<< SEARCH :10:",
            "old code",
            "=======",
            "new code {",
            "    with new lines",
            "}",
            ">>>>>>> REPLACE",
            "",
            "<<<<<<< SEARCH :12:",
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
        var args = MessageParser.Parse(BuiltInToolEnum.ApplyDiff, lines);

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

    [Fact]
    public void Parse_MCP_Blocks()
    {
        // Arrange
        var lines = """
            {
              "thought": "Проверка работы инструмента sequential-thinking со всеми возможными параметрами",
              "nextThoughtNeeded": true,
              "thoughtNumber": 1,
              "totalThoughts": 5,
              "isRevision": false,
              "revisesThought": null,
              "branchFromThought": null,
              "obj": {
                "before": ["one"],
                "after": ["2"]
              },
              "branchId": "test-branch",
              "needsMoreThoughts": true
            }
            """
            .Split('\n').ToList();

        var expectedObj = new
        {
            thought = "Проверка работы инструмента sequential-thinking со всеми возможными параметрами",
            nextThoughtNeeded = true,
            thoughtNumber = 1,
            totalThoughts = 5,
            isRevision = false,
            obj = new
            {
                before = new List<string> { "one" },
                after = new List<string> { "2" }
            },
            branchId = "test-branch",
            needsMoreThoughts = true
        };

        // Act
        var toolParams = MessageParser.Parse("mcp__sequential-thinking__sequential-thinking", lines);

        var args = JsonUtils.DeserializeParameters(string.Join('\n', toolParams.Values))
            .Where(x => x.Value is not null)
            .ToDictionary();

        // Assert
        Assert.Equal(8, args.Count);
        Assert.Equivalent(JsonUtils.Serialize(expectedObj), JsonUtils.Serialize(args));
    }
}

using Moq;
using UIBlazor.Components.Chat;
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
}

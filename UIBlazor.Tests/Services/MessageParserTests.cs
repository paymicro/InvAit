namespace UIBlazor.Tests.Services;

public class MessageParserTests
{
    private readonly MessageParser _parser;

    public MessageParserTests()
    {
        _parser = new MessageParser();
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
}

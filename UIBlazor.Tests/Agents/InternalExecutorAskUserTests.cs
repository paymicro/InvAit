namespace UIBlazor.Tests.Agents;

public partial class InternalExecutorTests
{
    [Fact]
    public async Task ExecuteToolAsync_AskUser_WithComplexOptions_ReturnsAllOptionsInResult()
    {
        // Arrange - options with special characters
        var options = new[] { "Option 1: Hello \"World\"", "Option 2: <special> & chars", "Option 3: Path C:\\temp\\file.txt" };
        var args = JsonSerializer.Serialize(new { question = "Which option?", options });

        // Act
        var result = await _executor.ExecuteToolAsync(BasicEnum.AskUser, args, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(result.Result);
        Assert.Equal("Which option?", doc.RootElement.GetProperty("question").GetString());
        var resultOptions = doc.RootElement.GetProperty("options").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(3, resultOptions.Count);
        Assert.Equal(options[0], resultOptions[0]);
        Assert.Equal(options[1], resultOptions[1]);
        Assert.Equal(options[2], resultOptions[2]);
    }

    [Fact]
    public async Task ExecuteToolAsync_AskUser_WithEmptyOptions_ReturnsEmptyOptionsList()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new { question = "What?", options = Array.Empty<string>() });

        // Act
        var result = await _executor.ExecuteToolAsync(BasicEnum.AskUser, args, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(result.Result);
        Assert.Equal("What?", doc.RootElement.GetProperty("question").GetString());
        var resultOptions = doc.RootElement.GetProperty("options").EnumerateArray().ToList();
        Assert.Empty(resultOptions);
    }

    [Fact]
    public async Task ExecuteToolAsync_AskUser_WithNullOptions_ReturnsEmptyOptionsList()
    {
        // Arrange - options property is missing from JSON entirely
        var args = JsonSerializer.Serialize(new { question = "Why?" });

        // Act
        var result = await _executor.ExecuteToolAsync(BasicEnum.AskUser, args, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(result.Result);
        Assert.Equal("Why?", doc.RootElement.GetProperty("question").GetString());
        var resultOptions = doc.RootElement.GetProperty("options").EnumerateArray().ToList();
        Assert.Empty(resultOptions);
    }
}

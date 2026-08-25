namespace UIBlazor.Tests.Components.ToolViews;

using AngleSharp.Html.Dom;

/// <summary>
/// Tests for <see cref="ToolAskOptions"/>
/// </summary>
public class ToolAskOptionsTests : BunitContext
{
    public ToolAskOptionsTests()
    {
        Services.AddRadzenComponents();
        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);
    }

    private const string AskArgs = """
                                  {
                                    "question": "Which database should we use?",
                                    "options": ["PostgreSQL", "SQLite"]
                                  }
                                  """;

    #region Rendering Tests

    [Fact]
    public void ShouldRenderQuestion_AndOptionButtons()
    {
        // Act
        var cut = Render<ToolAskOptions>(parameters => parameters
            .Add(p => p.Args, AskArgs));

        // Assert
        Assert.Contains("Which database should we use?", cut.Find(".tool-ask-question").TextContent);
        var buttons = cut.FindAll(".tool-ask-buttons button");
        Assert.Equal(2, buttons.Count);
        Assert.Contains("PostgreSQL", buttons[0].TextContent);
        Assert.Contains("SQLite", buttons[1].TextContent);
    }

    [Fact]
    public void ShouldRenderCustomInput_WhenNotAnswered()
    {
        // Act
        var cut = Render<ToolAskOptions>(parameters => parameters
            .Add(p => p.Args, AskArgs));

        // Assert
        var input = cut.Find(".tool-ask-custom input");
        Assert.NotNull(input);
        Assert.Equal(SharedResource.YourOption, input.GetAttribute("placeholder"));
    }

    [Fact]
    public void ShouldShowAnswer_AndHideOptions_WhenAnswerParameterProvided()
    {
        // Act
        var cut = Render<ToolAskOptions>(parameters => parameters
            .Add(p => p.Args, AskArgs)
            .Add(p => p.Answer, "SQLite"));

        // Assert
        var answer = cut.Find(".tool-ask-answer");
        Assert.Contains("SQLite", answer.TextContent);
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".tool-ask-buttons"));
    }

    [Fact]
    public void ShouldHandleInvalidArgsGracefully()
    {
        // Act - unparseable args are silently ignored (JsonUtils returns defaults):
        // empty question, no option buttons, custom input still available
        var cut = Render<ToolAskOptions>(parameters => parameters
            .Add(p => p.Args, "just free form text"));

        // Assert
        Assert.Equal(string.Empty, cut.Find(".tool-ask-question").TextContent.Trim());
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".tool-ask-buttons"));
        Assert.NotNull(cut.Find(".tool-ask-custom input"));
    }

    #endregion

    #region Interaction Tests

    [Fact]
    public async Task ClickingOption_InvokesCallback_AndShowsAnswer()
    {
        // Arrange
        string? received = null;

        var cut = Render<ToolAskOptions>(parameters => parameters
            .Add(p => p.Args, AskArgs)
            .Add(p => p.OnOptionSelectedCallback,
                EventCallback.Factory.Create<string>(this, v => received = v)));

        var optionButtons = cut.FindAll(".tool-ask-buttons button");

        // Act
        await cut.InvokeAsync(() => optionButtons[1].Click());

        // Assert
        Assert.Equal("SQLite", received);

        // UI switches to answered state
        var answer = cut.Find(".tool-ask-answer");
        Assert.Contains("SQLite", answer.TextContent);
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".tool-ask-buttons"));
    }

    [Fact]
    public async Task SubmittingCustomText_InvokesCallback_WithTypedValue()
    {
        // Arrange
        string? received = null;

        var cut = Render<ToolAskOptions>(parameters => parameters
            .Add(p => p.Args, AskArgs)
            .Add(p => p.OnOptionSelectedCallback,
                EventCallback.Factory.Create<string>(this, v => received = v)));

        var input = cut.Find(".tool-ask-custom input");
        await cut.InvokeAsync(() => input.Change("My own option"));

        // Act - submit the custom-answer form
        await cut.InvokeAsync(() => ((IHtmlFormElement)cut.Find(".tool-ask-custom")).Submit());

        // Assert
        Assert.Equal("My own option", received);
        Assert.Contains("My own option", cut.Find(".tool-ask-answer").TextContent);
    }

    [Fact]
    public async Task SubmittingEmptyCustomText_IsIgnored()
    {
        // Arrange
        string? received = null;

        var cut = Render<ToolAskOptions>(parameters => parameters
            .Add(p => p.Args, AskArgs)
            .Add(p => p.OnOptionSelectedCallback,
                EventCallback.Factory.Create<string>(this, v => received = v)));

        // Act - submit without typing anything; the guard must reject it
        await cut.InvokeAsync(() => ((IHtmlFormElement)cut.Find(".tool-ask-custom")).Submit());

        // Assert
        Assert.Null(received);
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".tool-ask-answer"));
    }

    [Fact]
    public void SendButton_IsDisabled_WhenInputEmpty()
    {
        // Arrange & Act
        var cut = Render<ToolAskOptions>(parameters => parameters
            .Add(p => p.Args, AskArgs));

        var sendButtonInstance = cut.FindComponents<RadzenButton>()
            .First(b => b.Instance.Icon == "send");

        // Assert
        Assert.True(sendButtonInstance.Instance.Disabled);
    }

    [Fact]
    public void OptionButtons_Hidden_AfterAnswerGivenViaParameter()
    {
        // Act
        var cut = Render<ToolAskOptions>(parameters => parameters
            .Add(p => p.Answer, "already answered"));

        // Assert - answered state hides buttons entirely
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".tool-ask-buttons"));
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void ShouldRenderEmptyQuestion_WhenArgsMissing()
    {
        // Act
        var cut = Render<ToolAskOptions>();

        // Assert - no crash; question area present but empty options
        Assert.NotNull(cut.Find(".tool-ask-question"));
        Assert.Empty(cut.FindAll(".tool-ask-buttons button"));
    }

    [Fact]
    public void ShouldParsePartialJson_QuestionOnly()
    {
        // Act - streaming-style truncated JSON still yields the question
        var cut = Render<ToolAskOptions>(parameters => parameters
            .Add(p => p.Args, """{"question":"Pick a color","opt"""));

        // Assert
        Assert.Contains("Pick a color", cut.Find(".tool-ask-question").TextContent);
    }

    #endregion
}

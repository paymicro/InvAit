namespace UIBlazor.Tests.Components;

/// <summary>
/// Integration tests for the reasoning ("thinking") block: <see cref="MessageContent"/> with the
/// REAL <see cref="Details"/> component (only <see cref="MarkdownBlock"/> is stubbed).
/// <para>
/// The block is lazy: nothing is rendered while collapsed, so a long think-block costs nothing
/// during streaming. These tests pin that the enclosing component actually passes the flag and
/// that expanding brings the content back - the unit tests on <see cref="Details"/> alone cannot
/// catch a caller that forgets the parameter.
/// </para>
/// </summary>
public class ReasoningDetailsTests : BunitContext
{
    public ReasoningDetailsTests()
    {
        Services.AddRadzenComponents();
        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);

        ComponentFactories.AddStub<MarkdownBlock>(parameters =>
        {
            var content = parameters.Get(p => p.Content);
            return builder =>
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "class", "markdown-block-stub");
                builder.AddContent(2, content ?? "empty");
                builder.CloseElement();
            };
        });
    }

    [Fact]
    public void Reasoning_Collapsed_ContentNotRendered_HeaderVisible()
    {
        var message = new VisualChatMessage
        {
            Role = ChatMessageRole.Assistant,
            ReasoningContent = "длинная цепочка размышлений"
        };

        var cut = Render<MessageContent>(p => p.Add(x => x.Message, message));

        // The header stays: the user must be able to open the block.
        Assert.NotEmpty(cut.FindAll(".reasoning-details"));
        Assert.NotEmpty(cut.FindAll(".header"));

        // The body is absent - that is the whole point of the change.
        Assert.DoesNotContain("длинная цепочка размышлений", cut.Markup);
    }

    [Fact]
    public void Reasoning_Expanded_RendersContent()
    {
        var message = new VisualChatMessage
        {
            Role = ChatMessageRole.Assistant,
            ReasoningContent = "длинная цепочка размышлений"
        };

        var cut = Render<MessageContent>(p => p.Add(x => x.Message, message));

        // Act - the user opens the block.
        cut.Find(".reasoning-details .header").Click();

        // Assert
        Assert.Contains("длинная цепочка размышлений", cut.Markup);
    }

    [Fact]
    public void Reasoning_StreamingWhileCollapsed_AppearsOnExpand()
    {
        // The streaming case: text accumulates while the block is closed, then the user opens it.
        // The content must be current, not the snapshot from the moment it collapsed.
        var message = new VisualChatMessage
        {
            Role = ChatMessageRole.Assistant,
            ReasoningContent = "первая мысль"
        };

        var cut = Render<MessageContent>(p => p.Add(x => x.Message, message));
        Assert.DoesNotContain("первая мысль", cut.Markup);

        // A token arrives while the block is still collapsed.
        message.ReasoningContent = "первая мысль вторая мысль";
        cut.Render(p => p.Add(x => x.Message, message));

        // Act
        cut.Find(".reasoning-details .header").Click();

        // Assert - the fresh text, not the stale one.
        Assert.Contains("первая мысль вторая мысль", cut.Markup);
    }
}

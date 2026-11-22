namespace UIBlazor.Tests.Components.ToolViews;

using Microsoft.AspNetCore.Components.Web;

/// <summary>
/// Tests for <see cref="ToolBashCommand"/>
/// </summary>
public class ToolBashCommandTests : BunitContext
{
    public ToolBashCommandTests()
    {
        Services.AddRadzenComponents();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void ShouldHighlightCommand_NotRawText()
    {
        // Act
        Render<ToolBashCommand>(parameters => parameters
            .Add(p => p.Args, """{"command":"echo hi","description":"Проверка"}"""));

        // Assert - контент уходит в JS параметром, элемент остаётся пустым (Blazor-owned)
        var invocation = JSInterop.VerifyInvoke("highlightCodeElement");
        Assert.Equal("bash", invocation.Arguments[1]);
        Assert.Equal("echo hi", invocation.Arguments[2]);
    }

    [Fact]
    public void ShouldNotRehighlight_WhenCommandUnchanged()
    {
        // Arrange
        const string args = """{"command":"echo hi","description":"Проверка"}""";
        var cut = Render<ToolBashCommand>(parameters => parameters.Add(p => p.Args, args));

        // Act - ре-рендер с тем же значением (растёт только описание)
        cut.Render(parameters => parameters
            .Add(p => p.Args, """{"command":"echo hi","description":"Проверка, но подробнее"}"""));

        // Assert - один вызов подсветки на команду, а не на каждый чанк args
        JSInterop.VerifyInvoke("highlightCodeElement", calledTimes: 1);
    }

    [Fact]
    public void ShouldRenderCommandWithoutDescription_WhenDescriptionMissing()
    {
        // Arrange - старые сессии и провайдеры без strict-режима: поля может не быть
        var cut = Render<ToolBashCommand>(parameters => parameters
            .Add(p => p.Args, """{"command":"ls"}"""));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find("[data-bash-description]"));
        Assert.NotNull(cut.Find("pre code"));
    }

    [Fact]
    public void ShouldRenderNothing_WhenArgsEmpty()
    {
        // Act
        var cut = Render<ToolBashCommand>(parameters => parameters.Add(p => p.Args, string.Empty));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".bash-command-view"));
    }

    [Fact]
    public void ShouldRenderDescriptionOnly_WhileCommandStillStreaming()
    {
        // Arrange - JSON ещё не закрыт: команды нет, описание уже пришло
        var cut = Render<ToolBashCommand>(parameters => parameters
            .Add(p => p.Args, """{"description":"Показывает файлы","command":"""));

        // Assert
        Assert.NotNull(cut.Find("[data-bash-description]"));
        Assert.Throws<ElementNotFoundException>(() => cut.Find("pre code"));
    }
}

namespace UIBlazor.Tests.Components;

/// <summary>
/// Tests for <see cref="Details"/>
/// </summary>
public class DetailsTests : BunitContext
{
    public DetailsTests()
    {
        Services.AddRadzenComponents();
    }

    #region Rendering Tests

    [Fact]
    public void ShouldRenderContainer()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.Text, "Header")
            .AddChildContent("<div class=\"inner\">content</div>"));

        // Assert
        Assert.NotNull(cut.Find(".custom-details"));
        Assert.NotNull(cut.Find(".header"));
        Assert.NotNull(cut.Find(".content-wrapper"));
        Assert.NotNull(cut.Find(".content-inner"));
    }

    [Fact]
    public void ShouldRenderHeaderText()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.Text, "My Section")
            .AddChildContent("content"));

        // Assert
        var headerLeft = cut.Find(".header-left");
        Assert.Contains("My Section", headerLeft.TextContent);
    }

    [Fact]
    public void ShouldRenderChildContent()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .AddChildContent("<div class=\"child-marker\">child content</div>"));

        // Assert
        var child = cut.Find(".child-marker");
        Assert.Equal("child content", child.TextContent);
    }

    #endregion

    #region Header Template Tests

    [Fact]
    public void ShouldRenderHeaderTemplate_WhenProvided()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.Text, "Ignored Text")
            .Add(p => p.HeaderTemplate, "<span class=\"tpl-marker\">template header</span>")
            .AddChildContent("content"));

        // Assert
        Assert.NotNull(cut.Find(".header-left .tpl-marker"));
        Assert.Contains("template header", cut.Find(".header-left").TextContent);
    }

    [Fact]
    public void HeaderTemplate_TakesPrecedenceOverTextAndIcon()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.Text, "Ignored Text")
            .Add(p => p.Icon, "fa-solid fa-lightbulb")
            .Add(p => p.HeaderTemplate, "<span class=\"tpl-marker\">custom</span>")
            .AddChildContent("content"));

        // Assert
        Assert.Contains("custom", cut.Find(".header-left").TextContent);
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".header-left .icon-spacing"));
        Assert.DoesNotContain("Ignored Text", cut.Find(".header-left").TextContent);
    }

    #endregion

    #region Icon Tests

    [Fact]
    public void ShouldRenderIcon_WhenIconProvided()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.Icon, "fa-solid fa-lightbulb")
            .AddChildContent("content"));

        // Assert - class attribute is split into individual CSS classes
        var icon = cut.Find("i.icon-spacing");
        Assert.Contains("fa-solid", icon.ClassList);
        Assert.Contains("fa-lightbulb", icon.ClassList);
    }

    [Fact]
    public void ShouldNotRenderIcon_WhenIconNotProvided()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .AddChildContent("content"));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find("i.icon-spacing"));
    }

    #endregion

    #region CSS State Tests

    [Fact]
    public void ShouldNotHaveExpandedClass_ByDefault()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .AddChildContent("content"));

        // Assert
        var container = cut.Find(".custom-details");
        Assert.DoesNotContain("is-expanded", container.ClassList);
    }

    [Fact]
    public void ShouldHaveExpandedClass_WhenIsExpandedIsTrue()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.IsExpanded, true)
            .AddChildContent("content"));

        // Assert
        var container = cut.Find(".custom-details");
        Assert.Contains("is-expanded", container.ClassList);
    }

    [Fact]
    public void ShouldHaveRoundedClass_WhenIsRoundedIsTrue()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.IsRounded, true)
            .AddChildContent("content"));

        // Assert
        var container = cut.Find(".custom-details");
        Assert.Contains("is-rounded", container.ClassList);
    }

    [Fact]
    public void ShouldAppendCustomClass_FromAdditionalAttributes()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.AdditionalAttributes, new Dictionary<string, object> { ["class"] = "reasoning-details" })
            .AddChildContent("content"));

        // Assert
        var container = cut.Find(".custom-details.reasoning-details");
        Assert.NotNull(container);
    }

    #endregion

    #region Interaction Tests

    [Fact]
    public async Task ClickHeader_TogglesExpandedState()
    {
        // Arrange - collapsed by default
        var cut = Render<Details>(parameters => parameters
            .AddChildContent("content"));

        var header = cut.Find(".header");

        // Act
        await cut.InvokeAsync(() => header.Click());

        // Assert
        Assert.Contains("is-expanded", cut.Find(".custom-details").ClassList);

        // Act - toggle back
        await cut.InvokeAsync(() => cut.Find(".header").Click());

        // Assert
        Assert.DoesNotContain("is-expanded", cut.Find(".custom-details").ClassList);
    }

    [Fact]
    public async Task ClickHeader_RemovesExpandedClass_WhenInitiallyExpanded()
    {
        // Arrange
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.IsExpanded, true)
            .AddChildContent("content"));

        // Act
        await cut.InvokeAsync(() => cut.Find(".header").Click());

        // Assert
        Assert.DoesNotContain("is-expanded", cut.Find(".custom-details").ClassList);
    }

    [Fact]
    public async Task IsExpanded_IsInitialValueOnly_ParentRerenderDoesNotResetUserToggle()
    {
        // Arrange - развернут изначально
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.IsExpanded, true)
            .AddChildContent("content"));

        // Act - пользователь сворачивает...
        await cut.InvokeAsync(() => cut.Find(".header").Click());
        Assert.DoesNotContain("is-expanded", cut.Find(".custom-details").ClassList);

        // ...а родитель перерендеривается, снова передавая IsExpanded=true (напр. стриминг)
        cut.Render(parameters => parameters
            .Add(p => p.IsExpanded, true)
            .AddChildContent("content"));

        // Assert - выбор пользователя сохранен
        Assert.DoesNotContain("is-expanded", cut.Find(".custom-details").ClassList);
    }

    #endregion

    #region Controlled Mode (OnToggle) Tests

    [Fact]
    public async Task OnToggle_ClickDelegatesToCallback_DoesNotChangeInternalState()
    {
        // Arrange - контролируемый режим: источник истины родитель
        var received = default(bool?);
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.IsExpanded, false)
            .Add(p => p.OnToggle, (Action<bool>)(expanded => received = expanded))
            .AddChildContent("content"));

        // Act
        await cut.InvokeAsync(() => cut.Find(".header").Click());

        // Assert - колбэк вызван с инвертированным значением,
        // внутреннее состояние НЕ меняется напрямую (родитель перерендерится)
        Assert.True(received);
        Assert.DoesNotContain("is-expanded", cut.Find(".custom-details").ClassList);
    }

    [Fact]
    public void OnToggle_ExternalParameterChange_AppliesToDetails()
    {
        // Arrange - контролируемый режим: OnParametersSet синхронизирует
        // внутреннее состояние с внешним IsExpanded при каждом родительском рендере
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.IsExpanded, false)
            .Add(p => p.OnToggle, (Action<bool>)(_ => { }))
            .AddChildContent("content"));

        Assert.DoesNotContain("is-expanded", cut.Find(".custom-details").ClassList);

        // Act - родитель принудительно раскрывает (напр. апрув сабагента)
        cut.Render(parameters => parameters
            .Add(p => p.IsExpanded, true)
            .Add(p => p.OnToggle, (Action<bool>)(_ => { }))
            .AddChildContent("content"));

        // Assert
        Assert.Contains("is-expanded", cut.Find(".custom-details").ClassList);
    }

    [Fact]
    public async Task OnToggle_ExternalCollapse_AppliesToDetails()
    {
        // Arrange - развернут, родитель сворачивает (напр. Finish сабагента)
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.IsExpanded, true)
            .Add(p => p.OnToggle, (Action<bool>)(_ => { }))
            .AddChildContent("content"));

        Assert.Contains("is-expanded", cut.Find(".custom-details").ClassList);

        // Act
        cut.Render(parameters => parameters
            .Add(p => p.IsExpanded, false)
            .Add(p => p.OnToggle, (Action<bool>)(_ => { }))
            .AddChildContent("content"));

        // Assert
        Assert.DoesNotContain("is-expanded", cut.Find(".custom-details").ClassList);
    }

    [Fact]
    public async Task OnToggle_Removed_RevertsToUncontrolledMode()
    {
        // Arrange - контролируемый режим
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.IsExpanded, false)
            .Add(p => p.OnToggle, (Action<bool>)(_ => { }))
            .AddChildContent("content"));

        // Act - колбэк снят (явный null: непереданный параметр в Blazor
        // сохраняет прежнее значение): компонент снова неконтролируемый,
        // клик меняет внутреннее состояние напрямую
        cut.Render(parameters => parameters
            .Add(p => p.IsExpanded, false)
            .Add(p => p.OnToggle, (Action<bool>?)null)
            .AddChildContent("content"));
        await cut.InvokeAsync(() => cut.Find(".header").Click());

        // Assert
        Assert.Contains("is-expanded", cut.Find(".custom-details").ClassList);
    }

    #endregion

    #region Parameter Update Tests

    [Fact]
    public void ShouldUpdateText_WhenParameterChanges()
    {
        // Arrange
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.Text, "First")
            .AddChildContent("content"));

        // Act
        cut.Render(parameters => parameters
            .Add(p => p.Text, "Second")
            .AddChildContent("content"));

        // Assert
        Assert.Contains("Second", cut.Find(".header-left").TextContent);
    }

    [Fact]
    public void ShouldRenderEmptyText_WhenTextNotProvided()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .AddChildContent("content"));

        // Assert - renders without error, only arrow in header
        var headerLeft = cut.Find(".header-left");
        Assert.Equal(string.Empty, headerLeft.TextContent.Trim());
    }

    #endregion
}

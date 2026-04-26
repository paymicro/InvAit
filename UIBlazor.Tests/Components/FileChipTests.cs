namespace UIBlazor.Tests.Components;

/// <summary>
/// Tests for <see cref="FileChip"/>
/// </summary>
public class FileChipTests : BunitContext
{
    public FileChipTests()
    {
        Services.AddRadzenComponents();
    }

    #region Rendering Tests

    [Fact]
    public void ShouldRenderChipContainer()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\path\\to\\file.cs",
            FileName = "file.cs"
        };

        // Act
        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token));

        // Assert
        var chip = cut.Find(".input-chip");
        Assert.NotNull(chip);
    }

    [Fact]
    public void ShouldRenderIcon()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\path\\to\\file.cs",
            FileName = "file.cs"
        };

        // Act
        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token)
            .Add(p => p.Icon, "📄"));

        // Assert
        var iconSpan = cut.Find(".chip-icon");
        Assert.NotNull(iconSpan);
        Assert.Equal("📄", iconSpan.TextContent);
    }

    [Fact]
    public void ShouldRenderFileName()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\path\\to\\MyFile.cs",
            FileName = "MyFile.cs"
        };

        // Act
        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token));

        // Assert
        var nameSpan = cut.Find(".chip-name");
        Assert.NotNull(nameSpan);
        Assert.Equal("MyFile.cs", nameSpan.TextContent);
    }

    [Fact]
    public void ShouldRenderRemoveButton()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\path\\to\\file.cs",
            FileName = "file.cs"
        };

        // Act
        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token));

        // Assert
        var removeButton = cut.Find(".chip-remove");
        Assert.NotNull(removeButton);
        Assert.Equal("Remove", removeButton.GetAttribute("title"));
        Assert.Equal("×", removeButton.TextContent);
    }

    [Fact]
    public void ShouldRenderDefaultIcon_WhenIconNotProvided()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\path\\to\\file.cs",
            FileName = "file.cs"
        };

        // Act - don't set Icon parameter
        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token));

        // Assert - should use default icon "📄"
        var iconSpan = cut.Find(".chip-icon");
        Assert.Equal("📄", iconSpan.TextContent);
    }

    [Fact]
    public void ShouldRenderCustomIcon_WhenIconProvided()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\path\\to\\file.razor",
            FileName = "file.razor"
        };

        // Act
        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token)
            .Add(p => p.Icon, "⚡"));

        // Assert
        var iconSpan = cut.Find(".chip-icon");
        Assert.Equal("⚡", iconSpan.TextContent);
    }

    [Fact]
    public void ShouldRenderCorrectStructure()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\path\\to\\file.cs",
            FileName = "file.cs"
        };

        // Act
        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token)
            .Add(p => p.Icon, "📄"));

        // Assert - verify all elements are present
        var chip = cut.Find(".input-chip");
        Assert.NotNull(chip);
        var iconSpan = cut.Find(".chip-icon");
        Assert.NotNull(iconSpan);
        var nameSpan = cut.Find(".chip-name");
        Assert.NotNull(nameSpan);
        var removeButton = cut.Find(".chip-remove");
        Assert.NotNull(removeButton);
    }

    #endregion

    #region Parameter Tests

    [Fact]
    public void ShouldDisplayTokenFileName()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\Projects\\MyApp\\Services\\UserService.cs",
            FileName = "UserService.cs"
        };

        // Act
        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token));

        // Assert
        var nameSpan = cut.Find(".chip-name");
        Assert.Equal("UserService.cs", nameSpan.TextContent);
    }

    [Fact]
    public void ShouldDisplayFileName_WithSpecialCharacters()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\path\\to\\my-file.config.json",
            FileName = "my-file.config.json"
        };

        // Act
        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token));

        // Assert
        var nameSpan = cut.Find(".chip-name");
        Assert.Equal("my-file.config.json", nameSpan.TextContent);
    }

    [Fact]
    public void ShouldDisplayFileName_WithUnicode()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\путь\\к\\файлу.cs",
            FileName = "файлу.cs"
        };

        // Act
        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token));

        // Assert
        var nameSpan = cut.Find(".chip-name");
        Assert.Equal("файлу.cs", nameSpan.TextContent);
    }

    [Fact]
    public void ShouldDisplayFileName_WithLongName()
    {
        // Arrange
        var longFileName = "VeryLongFileNameThatExceedsNormalLengthAndShouldBeTruncatedWithEllipsis.cs";
        var token = new FileToken
        {
            FilePath = $"C:\\path\\to\\{longFileName}",
            FileName = longFileName
        };

        // Act
        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token));

        // Assert - file name should be displayed (CSS handles truncation)
        var nameSpan = cut.Find(".chip-name");
        Assert.Equal(longFileName, nameSpan.TextContent);
    }

    [Fact]
    public void ShouldUpdateDisplay_WhenTokenChanges()
    {
        // Arrange
        var token1 = new FileToken
        {
            FilePath = "C:\\path\\to\\FirstFile.cs",
            FileName = "FirstFile.cs"
        };

        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token1));

        // Assert initial state
        var nameSpan = cut.Find(".chip-name");
        Assert.Equal("FirstFile.cs", nameSpan.TextContent);

        // Act - change token
        var token2 = new FileToken
        {
            FilePath = "C:\\path\\to\\SecondFile.cs",
            FileName = "SecondFile.cs"
        };

        cut.Render(parameters => parameters
            .Add(p => p.Token, token2));

        // Assert - should display new file name
        nameSpan = cut.Find(".chip-name");
        Assert.Equal("SecondFile.cs", nameSpan.TextContent);
    }

    [Fact]
    public void ShouldUpdateDisplay_WhenIconChanges()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\path\\to\\file.cs",
            FileName = "file.cs"
        };

        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token)
            .Add(p => p.Icon, "📄"));

        // Assert initial icon
        var iconSpan = cut.Find(".chip-icon");
        Assert.Equal("📄", iconSpan.TextContent);

        // Act - change icon
        cut.Render(parameters => parameters
            .Add(p => p.Token, token)
            .Add(p => p.Icon, "⚡"));

        // Assert - should display new icon
        iconSpan = cut.Find(".chip-icon");
        Assert.Equal("⚡", iconSpan.TextContent);
    }

    #endregion

    #region Event Tests

    [Fact]
    public async Task ShouldInvokeOnRemoveClick_WhenRemoveButtonClicked()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\path\\to\\file.cs",
            FileName = "file.cs"
        };

        var removeClicked = false;
        FileToken? receivedToken = null;

        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token)
            .Add(p => p.OnRemoveClick, EventCallback.Factory.Create<FileToken>(this, t =>
            {
                removeClicked = true;
                receivedToken = t;
            })));

        // Act
        var removeButton = cut.Find(".chip-remove");
        await cut.InvokeAsync(() => removeButton.Click());

        // Assert
        Assert.True(removeClicked);
        Assert.NotNull(receivedToken);
        Assert.Equal(token, receivedToken);
    }

    [Fact]
    public async Task ShouldPassCorrectToken_ToOnRemoveClick()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\Projects\\MyApp\\Program.cs",
            FileName = "Program.cs",
            FileContent = "file content here"
        };

        FileToken? receivedToken = null;

        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token)
            .Add(p => p.OnRemoveClick, EventCallback.Factory.Create<FileToken>(this, t => receivedToken = t)));

        // Act
        var removeButton = cut.Find(".chip-remove");
        await cut.InvokeAsync(() => removeButton.Click());

        // Assert
        Assert.NotNull(receivedToken);
        Assert.Equal("C:\\Projects\\MyApp\\Program.cs", receivedToken.FilePath);
        Assert.Equal("Program.cs", receivedToken.FileName);
        Assert.Equal("file content here", receivedToken.FileContent);
    }

    [Fact]
    public async Task ShouldNotThrow_WhenOnRemoveClickNotProvided()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\path\\to\\file.cs",
            FileName = "file.cs"
        };

        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token));
        // OnRemoveClick not set

        // Act & Assert - should not throw
        var removeButton = cut.Find(".chip-remove");
        var exception = await Record.ExceptionAsync(() => cut.InvokeAsync(() => removeButton.Click()));
        Assert.Null(exception);
    }

    [Fact]
    public async Task ShouldInvokeOnRemoveClick_MultipleTimes()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\path\\to\\file.cs",
            FileName = "file.cs"
        };

        var clickCount = 0;

        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token)
            .Add(p => p.OnRemoveClick, EventCallback.Factory.Create<FileToken>(this, _ => clickCount++)));

        // Act - click multiple times
        var removeButton = cut.Find(".chip-remove");
        await cut.InvokeAsync(() => removeButton.Click());
        await cut.InvokeAsync(() => removeButton.Click());
        await cut.InvokeAsync(() => removeButton.Click());

        // Assert
        Assert.Equal(3, clickCount);
    }

    #endregion

    #region CSS Class Tests

    [Fact]
    public void ShouldHaveInputChipClass()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\path\\to\\file.cs",
            FileName = "file.cs"
        };

        // Act
        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token));

        // Assert
        var chip = cut.Find("div.input-chip");
        Assert.NotNull(chip);
    }

    [Fact]
    public void ShouldHaveChipIconClass_OnIconSpan()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\path\\to\\file.cs",
            FileName = "file.cs"
        };

        // Act
        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token));

        // Assert
        var iconSpan = cut.Find("span.chip-icon");
        Assert.NotNull(iconSpan);
    }

    [Fact]
    public void ShouldHaveChipNameClass_OnNameSpan()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\path\\to\\file.cs",
            FileName = "file.cs"
        };

        // Act
        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token));

        // Assert
        var nameSpan = cut.Find("span.chip-name");
        Assert.NotNull(nameSpan);
    }

    [Fact]
    public void ShouldHaveChipRemoveClass_OnRemoveButton()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\path\\to\\file.cs",
            FileName = "file.cs"
        };

        // Act
        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token));

        // Assert
        var removeButton = cut.Find("button.chip-remove");
        Assert.NotNull(removeButton);
    }

    [Fact]
    public void RemoveButton_ShouldHaveCorrectTitle()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\path\\to\\file.cs",
            FileName = "file.cs"
        };

        // Act
        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token));

        // Assert
        var removeButton = cut.Find(".chip-remove");
        Assert.Equal("Remove", removeButton.GetAttribute("title"));
    }

    #endregion

    #region Icon Variations Tests

    [Theory]
    [InlineData("📄", "document")]
    [InlineData("⚡", "razor")]
    [InlineData("🌐", "html")]
    [InlineData("🎨", "css")]
    [InlineData("📜", "javascript")]
    [InlineData("📋", "json")]
    [InlineData("📝", "text")]
    [InlineData("📖", "markdown")]
    [InlineData("🖼️", "image")]
    [InlineData("⚙️", "config")]
    [InlineData("📦", "project")]
    public void ShouldRenderVariousIcons(string icon, string description)
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\path\\to\\file.ext",
            FileName = $"file.{description}"
        };

        // Act
        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token)
            .Add(p => p.Icon, icon));

        // Assert
        var iconSpan = cut.Find(".chip-icon");
        Assert.Equal(icon, iconSpan.TextContent);
    }

    #endregion

    #region FileToken Properties Tests

    [Fact]
    public void ShouldWork_WithTokenHavingEmptyFilePath()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "",
            FileName = "OnlyName.cs"
        };

        // Act
        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token));

        // Assert - should render without errors
        var nameSpan = cut.Find(".chip-name");
        Assert.Equal("OnlyName.cs", nameSpan.TextContent);
    }

    [Fact]
    public void ShouldWork_WithTokenHavingFileContent()
    {
        // Arrange
        var token = new FileToken
        {
            FilePath = "C:\\path\\to\\file.cs",
            FileName = "file.cs",
            FileContent = "namespace Test { }"
        };

        // Act
        var cut = Render<FileChip>(parameters => parameters
            .Add(p => p.Token, token));

        // Assert - should render without errors (FileContent is not displayed in UI)
        var nameSpan = cut.Find(".chip-name");
        Assert.Equal("file.cs", nameSpan.TextContent);
    }

    #endregion
}

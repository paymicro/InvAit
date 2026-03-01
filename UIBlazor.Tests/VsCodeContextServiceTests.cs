using Shared.Contracts;
using UIBlazor.Services;
using UIBlazor.Services.Models;

namespace UIBlazor.Tests;

public class VsCodeContextServiceTests
{
    private readonly VsCodeContextService _service;

    public VsCodeContextServiceTests()
    {
        _service = new VsCodeContextService();
    }

    [Fact]
    public void UpdateContext_SetsCurrentContext_AndNotifies()
    {
        // Arrange
        var context = new VsCodeContext { ActiveFilePath = "test.cs" };
        var eventFired = false;
        _service.OnContextChanged += () => eventFired = true;

        // Act
        _service.UpdateContext(context);

        // Assert
        Assert.Equal(context, _service.CurrentContext);
        Assert.True(eventFired);
    }
}

namespace UIBlazor.Tests.Models;

public class SubAgentMessageTests
{
    [Fact]
    public void SubAgentMessage_DefaultValues_AreCorrect()
    {
        // Act
        var msg = new SubAgentMessage();

        // Assert
        Assert.NotEmpty(msg.Id);
        Assert.Equal(SubAgentStatus.Pending, msg.Status);
        Assert.Empty(msg.Task);
        Assert.Empty(msg.SystemPrompt);
        Assert.Empty(msg.Result);
        Assert.Empty(msg.Messages);
        Assert.Null(msg.AllowedTools);
        Assert.Null(msg.ErrorMessage);
        Assert.Null(msg.CompletedAt);
        Assert.Equal(0, msg.TotalTokens);
        Assert.False(msg.IsExpanded);
    }

    [Fact]
    public void SubAgentMessage_Id_IsUnique()
    {
        // Act
        var msg1 = new SubAgentMessage();
        var msg2 = new SubAgentMessage();

        // Assert
        Assert.NotEqual(msg1.Id, msg2.Id);
    }

    [Fact]
    public void SubAgentMessage_NotifyStateChanged_RaisesEvent()
    {
        // Arrange
        var msg = new SubAgentMessage();
        var eventRaised = false;
        msg.StateChanged += () => eventRaised = true;

        // Act
        msg.NotifyStateChanged();

        // Assert
        Assert.True(eventRaised);
    }

    [Fact]
    public void SubAgentMessage_NotifyStateChanged_NoSubscribers_DoesNotThrow()
    {
        // Arrange
        var msg = new SubAgentMessage();

        // Act & Assert - should not throw
        msg.NotifyStateChanged();
    }

    [Fact]
    public void SubAgentMessage_MultipleSubscribers_AllNotified()
    {
        // Arrange
        var msg = new SubAgentMessage();
        var count = 0;
        msg.StateChanged += () => count++;
        msg.StateChanged += () => count++;

        // Act
        msg.NotifyStateChanged();

        // Assert
        Assert.Equal(2, count);
    }

    [Fact]
    public void SubAgentMessage_SetProperties_ValuesPersist()
    {
        // Arrange & Act
        var msg = new SubAgentMessage
        {
            Task = "Analyze code",
            SystemPrompt = "You are a code analyzer.",
            AllowedTools = ["read_files", "grep"],
            Status = SubAgentStatus.Running,
            IsExpanded = true,
            Result = "Analysis complete",
            TotalTokens = 500,
            ErrorMessage = "Something went wrong"
        };

        // Assert
        Assert.Equal("Analyze code", msg.Task);
        Assert.Equal("You are a code analyzer.", msg.SystemPrompt);
        Assert.Equal(["read_files", "grep"], msg.AllowedTools);
        Assert.Equal(SubAgentStatus.Running, msg.Status);
        Assert.True(msg.IsExpanded);
        Assert.Equal("Analysis complete", msg.Result);
        Assert.Equal(500, msg.TotalTokens);
        Assert.Equal("Something went wrong", msg.ErrorMessage);
    }

    [Fact]
    public void SubAgentStatus_AllValuesExist()
    {
        // Assert
        Assert.Equal(5, Enum.GetNames(typeof(SubAgentStatus)).Length);
        Assert.True(Enum.IsDefined(typeof(SubAgentStatus), "Pending"));
        Assert.True(Enum.IsDefined(typeof(SubAgentStatus), "Running"));
        Assert.True(Enum.IsDefined(typeof(SubAgentStatus), "Completed"));
        Assert.True(Enum.IsDefined(typeof(SubAgentStatus), "Cancelled"));
        Assert.True(Enum.IsDefined(typeof(SubAgentStatus), "Failed"));
    }
}

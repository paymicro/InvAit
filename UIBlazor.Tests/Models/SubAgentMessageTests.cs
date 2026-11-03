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
        Assert.Empty(msg.GetMessages());
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
    public void SubAgentMessage_Revision_StartsAtZero()
    {
        // Act
        var msg = new SubAgentMessage();

        // Assert
        Assert.Equal(0, msg.Revision);
    }

    [Fact]
    public void SubAgentMessage_NotifyStateChanged_IncrementsRevision()
    {
        // Arrange
        var msg = new SubAgentMessage();

        // Act
        msg.NotifyStateChanged();
        msg.NotifyStateChanged();
        var afterTwoNotifications = msg.Revision;
        msg.NotifyStateChanged();

        // Assert
        Assert.Equal(2, afterTwoNotifications);
        Assert.Equal(3, msg.Revision);
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
            TotalTokens = 500,
            ErrorMessage = "Something went wrong"
        };

        // Assert
        Assert.Equal("Analyze code", msg.Task);
        Assert.Equal("You are a code analyzer.", msg.SystemPrompt);
        Assert.Equal(["read_files", "grep"], msg.AllowedTools);
        Assert.Equal(SubAgentStatus.Running, msg.Status);
        Assert.True(msg.IsExpanded);
        Assert.Equal(500, msg.TotalTokens);
        Assert.Equal("Something went wrong", msg.ErrorMessage);
    }

    [Fact]
    public void SubAgentMessage_NeedAction_CanCancelAndInterject()
    {
        var msg = new SubAgentMessage { Status = SubAgentStatus.NeedAction };

        // NeedAction is still an active state: cancellable and accepts interjects
        Assert.True(msg.CanCancel);
        Assert.True(msg.TryAddInterject("hello"));
        Assert.False(msg.IsFinished);
    }

    [Fact]
    public void SubAgentMessage_IsFinished_TerminalStatesOnly()
    {
        Assert.False(new SubAgentMessage { Status = SubAgentStatus.Pending }.IsFinished);
        Assert.False(new SubAgentMessage { Status = SubAgentStatus.Running }.IsFinished);
        Assert.False(new SubAgentMessage { Status = SubAgentStatus.NeedAction }.IsFinished);
        Assert.False(new SubAgentMessage { Status = SubAgentStatus.Paused }.IsFinished);
        Assert.True(new SubAgentMessage { Status = SubAgentStatus.Completed }.IsFinished);
        Assert.True(new SubAgentMessage { Status = SubAgentStatus.Failed }.IsFinished);
        Assert.True(new SubAgentMessage { Status = SubAgentStatus.Cancelled }.IsFinished);
    }

    [Fact]
    public void SubAgentMessage_AttachSession_SetMessages_SyncsLocalCopy()
    {
        // Arrange: the branch session carries a running conversation.
        var subAgent = new SubAgentMessage();
        var session = new ConversationSession { Id = "subagent_test" };
        subAgent.AttachSession(session);
        subAgent.AddMessage(VisualChatMessage.CreateUser("task"));
        subAgent.AddMessage(new VisualChatMessage { Role = ChatMessageRole.Assistant, Content = "turn 1" });
        Assert.Equal(2, subAgent.GetMessageCount());

        // Act: context compression rewrites the session DIRECTLY via SetMessages
        // (bypassing the SubAgentMessage wrapper methods).
        var compressed = new List<VisualChatMessage>
        {
            VisualChatMessage.CreateUser("task"),
            new VisualChatMessage { Role = ChatMessageRole.Assistant, Content = "summary" }
        };
        session.SetMessages(compressed);

        // Assert: the UI-side copy is in sync with the model's (compressed) history —
        // the user sees the compressed history, not the stale uncompressed one.
        Assert.Equal(2, subAgent.GetMessageCount());
        Assert.Equal("summary", subAgent.GetMessages()[1].Content);
    }

    [Fact]
    public void SubAgentMessage_AttachSession_SessionSetMessages_DoesNotPropagateBack()
    {
        // Arrange
        var subAgent = new SubAgentMessage();
        var session = new ConversationSession { Id = "subagent_test" };
        subAgent.AttachSession(session);

        // Act: the session event handler updates the local copy only — no re-entry
        // into the session (a propagation loop would stack-overflow).
        session.SetMessages([VisualChatMessage.CreateUser("compressed")]);

        // Assert: exactly one message on both sides, no duplication from re-entry.
        Assert.Equal(1, subAgent.GetMessageCount());
        Assert.Equal(1, session.GetMessageCount());
    }

    [Fact]
    public void SubAgentMessage_AddMessage_AfterCompression_PropagatesToSession()
    {
        // Arrange: compression replaced the session history.
        var subAgent = new SubAgentMessage();
        var session = new ConversationSession { Id = "subagent_test" };
        subAgent.AttachSession(session);
        session.SetMessages([VisualChatMessage.CreateUser("compressed")]);

        // Act: the branch continues after compression — the new message must reach
        // BOTH the UI copy and the session (the wrapper still works after re-sync).
        subAgent.AddMessage(new VisualChatMessage { Role = ChatMessageRole.Assistant, Content = "next turn" });

        // Assert
        Assert.Equal(2, subAgent.GetMessageCount());
        Assert.Equal(2, session.GetMessageCount());
        Assert.Equal("next turn", session.GetMessagesSnapshot()[1].Content);
    }

    #region TryAddInterject Tests

    [Fact]
    public void TryAddInterject_Running_AddsMessageToSessionAndLocalCopy()
    {
        // Arrange: a running branch with an attached session.
        var subAgent = new SubAgentMessage { Status = SubAgentStatus.Running };
        var session = new ConversationSession { Id = "subagent_test" };
        subAgent.AttachSession(session);
        subAgent.AddMessage(VisualChatMessage.CreateUser("task"));
        var notified = false;
        subAgent.StateChanged += () => notified = true;

        // Act: the user writes to the branch while it works.
        var accepted = subAgent.TryAddInterject("  уточни требования  ");

        // Assert: accepted — the interject lands in BOTH the UI copy and the session
        // (the branch's AfAgentLoop reads the session at the next turn boundary).
        Assert.True(accepted);
        Assert.Equal(2, subAgent.GetMessageCount());
        Assert.Equal(2, session.GetMessageCount());
        var interject = subAgent.GetMessages()[1];
        Assert.Equal(ChatMessageRole.User, interject.Role);
        Assert.Equal("уточни требования", interject.Content);
        Assert.True(interject.IsInterject);
        Assert.True(notified);
    }

    [Fact]
    public void TryAddInterject_NotRunning_ReturnsFalse_NothingAdded()
    {
        // Arrange: a finished branch (insertion into finished/cancelled branches is rejected).
        var subAgent = new SubAgentMessage { Status = SubAgentStatus.Completed };
        var session = new ConversationSession { Id = "subagent_test" };
        subAgent.AttachSession(session);

        // Act
        var accepted = subAgent.TryAddInterject("late message");

        // Assert
        Assert.False(accepted);
        Assert.Empty(subAgent.GetMessages());
        Assert.Empty(session.GetMessagesSnapshot());
    }

    [Fact]
    public void TryAddInterject_Cancelled_ReturnsFalse_NothingAdded()
    {
        var subAgent = new SubAgentMessage { Status = SubAgentStatus.Cancelled };
        var session = new ConversationSession { Id = "subagent_test" };
        subAgent.AttachSession(session);

        var accepted = subAgent.TryAddInterject("late message");

        Assert.False(accepted);
        Assert.Empty(subAgent.GetMessages());
        Assert.Empty(session.GetMessagesSnapshot());
    }

    [Fact]
    public void TryAddInterject_Compressing_ReturnsFalse_NothingAdded()
    {
        // Arrange: a running branch that is compressing — compression rewrites the session
        // history from a snapshot and would silently drop the interject.
        var subAgent = new SubAgentMessage { Status = SubAgentStatus.Running, IsCompressing = true };
        var session = new ConversationSession { Id = "subagent_test" };
        subAgent.AttachSession(session);

        // Act
        var accepted = subAgent.TryAddInterject("message during compression");

        // Assert
        Assert.False(accepted);
        Assert.Empty(subAgent.GetMessages());
        Assert.Empty(session.GetMessagesSnapshot());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryAddInterject_EmptyOrWhitespace_ReturnsFalse_NothingAdded(string text)
    {
        var subAgent = new SubAgentMessage { Status = SubAgentStatus.Running };
        var session = new ConversationSession { Id = "subagent_test" };
        subAgent.AttachSession(session);

        var accepted = subAgent.TryAddInterject(text);

        Assert.False(accepted);
        Assert.Empty(subAgent.GetMessages());
        Assert.Empty(session.GetMessagesSnapshot());
    }

    #endregion

    [Fact]
    public void SubAgentStatus_AllValuesExist()
    {
        // Assert
        Assert.Equal(7, Enum.GetNames(typeof(SubAgentStatus)).Length);
        Assert.True(Enum.IsDefined(typeof(SubAgentStatus), "Pending"));
        Assert.True(Enum.IsDefined(typeof(SubAgentStatus), "Running"));
        Assert.True(Enum.IsDefined(typeof(SubAgentStatus), "NeedAction"));
        Assert.True(Enum.IsDefined(typeof(SubAgentStatus), "Paused"));
        Assert.True(Enum.IsDefined(typeof(SubAgentStatus), "Completed"));
        Assert.True(Enum.IsDefined(typeof(SubAgentStatus), "Cancelled"));
        Assert.True(Enum.IsDefined(typeof(SubAgentStatus), "Failed"));
    }
}

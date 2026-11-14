namespace UIBlazor.Tests.Models;

/// <summary>
/// The session model's "I changed" signal — the foundation of the autosave invariant.
/// Persistence subscribes to <c>Changed</c> instead of relying on every caller to call
/// SaveSessionAsync, so these tests pin exactly which mutations must raise it and, just as
/// important, that TotalTokens (updated on every streamed delta) must NOT.
/// </summary>
public class ConversationSessionChangedTests
{
    /// <summary>Subscribes and collects every raised notification (one entry per raise).</summary>
    private static List<ConversationSession> Track(ConversationSession session)
    {
        var raised = new List<ConversationSession>();
        session.Changed += changed => raised.Add(changed);
        return raised;
    }

    [Fact]
    public void AddMessage_RaisesChanged()
    {
        var session = new ConversationSession { Id = "s" };
        var raised = Track(session);

        session.AddMessage(VisualChatMessage.CreateUser("hello"));

        Assert.Single(raised);
        Assert.Same(session, raised[0]);
    }

    [Fact]
    public void RemoveMessage_RaisesChanged()
    {
        var session = new ConversationSession { Id = "s" };
        var message = VisualChatMessage.CreateUser("hello");
        session.AddMessage(message);
        var raised = Track(session);

        session.RemoveMessage(message);

        Assert.Single(raised);
    }

    [Fact]
    public void RemoveMessage_UnknownId_DoesNotRaiseChanged()
    {
        // A no-op removal (e.g. the retry path removing an already-released tail message)
        // must not schedule a pointless localStorage write.
        var session = new ConversationSession { Id = "s" };
        session.AddMessage(VisualChatMessage.CreateUser("hello"));
        var raised = Track(session);

        session.RemoveMessage("does-not-exist");

        Assert.Empty(raised);
    }

    [Fact]
    public void SetMessages_RaisesChanged()
    {
        var session = new ConversationSession { Id = "s" };
        var raised = Track(session);

        session.SetMessages([VisualChatMessage.CreateUser("compressed")]);

        Assert.Single(raised);
    }

    [Fact]
    public void UpdateMessage_RaisesChanged()
    {
        var session = new ConversationSession { Id = "s" };
        var message = VisualChatMessage.CreateUser("before");
        session.AddMessage(message);
        var raised = Track(session);

        session.UpdateMessage(message.Id, "after");

        Assert.Single(raised);
    }

    [Fact]
    public void ModeChange_RaisesChanged()
    {
        var session = new ConversationSession { Id = "s" };
        var raised = Track(session);

        session.Mode = AppMode.Agent;

        Assert.Single(raised);
    }

    [Fact]
    public void ModeSetToSameValue_DoesNotRaiseChanged()
    {
        var session = new ConversationSession { Id = "s", Mode = AppMode.Agent };
        var raised = Track(session);

        session.Mode = AppMode.Agent;

        Assert.Empty(raised);
    }

    /// <summary>
    /// TotalTokens is written on every streamed delta (usage capture + heuristic estimate).
    /// Raising Changed for it would turn autosave into a per-token localStorage writer.
    /// </summary>
    [Fact]
    public void TotalTokensChange_DoesNotRaiseChanged()
    {
        var session = new ConversationSession { Id = "s" };
        var raised = Track(session);

        session.TotalTokens = 12345;

        Assert.Empty(raised);
    }
}

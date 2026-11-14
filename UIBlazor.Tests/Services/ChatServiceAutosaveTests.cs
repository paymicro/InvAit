using System.Diagnostics;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Autosave as an INVARIANT of the session model: ConversationSession raises Changed on
/// structural mutations and ChatService persists them on its own. No caller has to remember
/// SaveSessionAsync — a run that fails, is cancelled or is still in flight cannot lose history.
/// These tests pin the guarantees: nothing is lost, bursts coalesce, failures retry, the
/// previous session is flushed before a swap, and TotalTokens-only changes never write.
/// </summary>
public partial class ChatServiceTests
{
    private sealed record SavedSession(string Id, int MessageCount, AppMode Mode, string LastContent, string Json);

    /// <summary>
    /// Records a snapshot of every persisted session, taken at write time. The JSON is captured
    /// too: asserting on the serialized payload (not only on the live object) is what proves the
    /// reload path works — the file is what the user actually gets back.
    /// </summary>
    private List<SavedSession> CaptureSessionSaves()
    {
        var saved = new List<SavedSession>();
        _localStorageMock
            .Setup(ls => ls.SetItemAsync(It.IsAny<string>(), It.IsAny<ConversationSession>()))
            .Callback<string, ConversationSession>((_, session) =>
            {
                var messages = session.GetMessagesSnapshot();
                var snapshot = new SavedSession(session.Id, messages.Count, session.Mode,
                    messages.Count == 0 ? string.Empty : messages[^1].Content,
                    JsonSerializer.Serialize(session));
                lock (saved)
                    saved.Add(snapshot);
            })
            .Returns(Task.CompletedTask);
        return saved;
    }

    private static List<SavedSession> Snapshot(List<SavedSession> saved)
    {
        lock (saved)
            return [.. saved];
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail($"Timed out waiting for: {because}");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task AddMessage_IsPersistedWithoutExplicitSaveOrInitialize()
    {
        // Arrange - neither Initialize() nor SaveSessionAsync() is called by the host. This is
        // the historical live bug: a run that threw inside the loop never reached the manual
        // save, so the history was lost on reload. Persistence must be an invariant of the model.
        var saved = CaptureSessionSaves();
        using var service = CreateChatService();
        service.AutosaveDelay = TimeSpan.Zero;
        var sessionId = service.Session.Id;

        // Act
        service.Session.AddMessage(VisualChatMessage.CreateUser("hello"));

        // Assert - the write happened AND the payload survives a reload (JSON round-trip).
        await WaitUntilAsync(
            () => Snapshot(saved).Exists(s => s.Id == sessionId && s.LastContent == "hello"),
            "the session to be autosaved with the new message");

        var json = Snapshot(saved).Single(s => s.Id == sessionId).Json;
        var reloaded = JsonSerializer.Deserialize<ConversationSession>(json);
        Assert.NotNull(reloaded);
        var reloadedMessage = Assert.Single(reloaded.GetMessagesSnapshot());
        Assert.Equal("hello", reloadedMessage.Content);
        Assert.Equal(ChatMessageRole.User, reloadedMessage.Role);
    }

    [Fact]
    public async Task BurstOfMutations_CoalescesIntoOneWriteOnFlush()
    {
        // Arrange - a tool-heavy turn produces several structural mutations in a burst. With a
        // debounce window that cannot elapse during the test, no write may happen on its own:
        // the burst must land as ONE write when the run flushes (deterministic, no sleeps).
        var saved = CaptureSessionSaves();
        using var service = CreateChatService();
        service.AutosaveDelay = TimeSpan.FromSeconds(30);

        for (var i = 0; i < 5; i++)
            service.Session.AddMessage(VisualChatMessage.CreateUser($"message {i}"));

        // Act
        Assert.Empty(Snapshot(saved)); // coalesced: nothing written mid-window
        await service.SaveSessionAsync();

        // Assert
        var write = Assert.Single(Snapshot(saved));
        Assert.Equal(5, write.MessageCount);
    }

    [Fact]
    public async Task SaveSessionAsync_FlushesPendingDebounceImmediately()
    {
        // Arrange - a very long debounce: only the explicit flush can produce a write.
        var saved = CaptureSessionSaves();
        using var service = CreateChatService();
        service.Initialize();
        service.AutosaveDelay = TimeSpan.FromSeconds(30);

        service.Session.AddMessage(VisualChatMessage.CreateUser("hello"));
        Assert.Empty(Snapshot(saved));

        // Act
        await service.SaveSessionAsync();

        // Assert
        var writes = Snapshot(saved);
        Assert.Single(writes);
        Assert.Equal("hello", writes[0].LastContent);
    }

    [Fact]
    public async Task ModeSwitch_IsPersisted()
    {
        // Arrange - mode lives in the session and survives a reload only if it is written.
        var saved = CaptureSessionSaves();
        using var service = CreateChatService();
        service.Initialize();
        service.AutosaveDelay = TimeSpan.Zero;
        var sessionId = service.Session.Id;

        // Act
        service.Session.Mode = AppMode.Agent;

        // Assert
        await WaitUntilAsync(
            () => Snapshot(saved).Exists(s => s.Id == sessionId && s.Mode == AppMode.Agent),
            "the mode switch to be autosaved");
    }


    [Fact]
    public async Task StorageFailure_DoesNotThrow_AndRetriesOnNextFlush()
    {
        // Arrange - the storage rejects the write (quota / interop failure).
        var attempts = 0;
        var failing = true;
        var succeeded = false;
        _localStorageMock
            .Setup(ls => ls.SetItemAsync(It.IsAny<string>(), It.IsAny<ConversationSession>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref attempts);
                if (failing)
                    return Task.FromException(new InvalidOperationException("storage down"));
                succeeded = true;
                return Task.CompletedTask;
            });

        using var service = CreateChatService();
        service.Initialize();
        service.AutosaveDelay = TimeSpan.Zero;

        // Act - the failed autosave must not surface as an unhandled exception...
        service.Session.AddMessage(VisualChatMessage.CreateUser("hello"));
        await WaitUntilAsync(() => Volatile.Read(ref attempts) >= 1, "the first (failing) write");

        // ...and the session must stay dirty, so the next flush retries the same session.
        failing = false;
        await service.SaveSessionAsync();

        // Assert - the retry happened (>= 2: the failed write plus at least one retry) and the
        // session was persisted. The exact attempt count is an internal scheduling detail.
        Assert.True(Volatile.Read(ref attempts) >= 2, "the failed write must be retried");
        Assert.True(succeeded, "the retry must succeed");
    }

    [Fact]
    public async Task NewSession_FlushesPreviousSessionUnderItsOwnId()
    {
        // Arrange - work in flight when the user starts a new session must not be lost.
        var saved = CaptureSessionSaves();
        using var service = CreateChatService();
        service.Initialize();
        service.AutosaveDelay = TimeSpan.FromSeconds(30);

        var previous = service.Session;
        previous.AddMessage(VisualChatMessage.CreateUser("work in progress"));
        Assert.Empty(Snapshot(saved));

        // Act
        await service.NewSessionAsync();

        // Assert
        Assert.Contains(Snapshot(saved), s => s.Id == previous.Id && s.LastContent == "work in progress");
        Assert.NotEqual(previous.Id, service.Session.Id);
    }

    [Fact]
    public async Task DeletedSession_IsNotResurrectedByPendingAutosave()
    {
        // Arrange - a session is dirty (write pending) when the user deletes it.
        var saved = CaptureSessionSaves();
        using var service = CreateChatService();
        service.Initialize();
        service.AutosaveDelay = TimeSpan.FromSeconds(30);

        var deletedId = service.Session.Id;
        service.Session.AddMessage(VisualChatMessage.CreateUser("doomed"));

        // Act
        await service.DeleteSessionAsync(deletedId);
        await service.SaveSessionAsync();

        // Assert - the pending autosave must not write the deleted session back to storage.
        Assert.DoesNotContain(Snapshot(saved), s => s.Id == deletedId);
    }

    /// <summary>
    /// Context compression rewrites the whole history via SetMessages (the /compact path).
    /// That rewrite must be persisted on its own: before the autosave invariant it was lost on
    /// reload, and the user silently got the uncompressed history back.
    /// </summary>
    [Fact]
    public async Task SetMessages_FromCompression_IsPersisted()
    {
        // Arrange
        var saved = CaptureSessionSaves();
        using var service = CreateChatService();
        service.AutosaveDelay = TimeSpan.Zero;
        var sessionId = service.Session.Id;
        service.Session.AddMessage(VisualChatMessage.CreateUser("old work"));
        service.Session.AddMessage(new VisualChatMessage { Role = ChatMessageRole.Assistant, Content = "old answer" });

        // Act - what CompressSessionCoreAsync does at the end.
        service.Session.SetMessages(
        [
            new VisualChatMessage { Role = ChatMessageRole.Assistant, Content = "summary" },
            VisualChatMessage.CreateUser("current task")
        ]);

        // Assert - the last write carries the compressed history, not the old one.
        await WaitUntilAsync(
            () => Snapshot(saved).Exists(s => s.Id == sessionId && s.Json.Contains("summary", StringComparison.Ordinal)),
            "the compressed history to be autosaved");
        var write = Snapshot(saved).Last(s => s.Id == sessionId);
        Assert.Equal(2, write.MessageCount);
        Assert.DoesNotContain("old work", write.Json);
    }

}

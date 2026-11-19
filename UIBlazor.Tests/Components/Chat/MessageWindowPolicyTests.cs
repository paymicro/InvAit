using UIBlazor.Components.Chat;

namespace UIBlazor.Tests.Components.Chat;

/// <summary>
/// Tests for <see cref="MessageWindowPolicy"/>.
/// <para>
/// The policy is pure, so everything here is asserted on the plan - no bUnit, no renderer. That is
/// deliberate: the failures this policy prevents (a freeze on a 500-message load, an approval gate
/// swallowed by the fold, a boundary that jumps under the reader) are all consequences of the plan
/// shape, and the plan is where they must be caught.
/// </para>
/// </summary>
public class MessageWindowPolicyTests
{
    #region Helpers

    private static List<VisualChatMessage> BuildHistory(int count, string? content = null)
    {
        var messages = new List<VisualChatMessage>(count);

        for (var i = 0; i < count; i++)
        {
            messages.Add(new VisualChatMessage
            {
                Role = i % 2 == 0 ? ChatMessageRole.User : ChatMessageRole.Assistant,
                Content = content ?? $"message {i}"
            });
        }

        return messages;
    }

    private static ToolCall CreateToolCall(
        string name = "read_file",
        ToolApprovalStatus? approval = ToolApprovalStatus.Approved,
        string arguments = "{}",
        string? resultContent = "ok")
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            Function = new ToolCallFunction { Name = name, Arguments = arguments },
            ApprovalStatus = approval,
            Result = resultContent is null
                ? null
                : new ToolResult { Name = name, Content = resultContent, Success = true }
        };

    private static ToolCall CreateDelegateTaskCall(params SubAgentStatus[] branchStatuses)
    {
        var call = CreateToolCall(name: BuiltInToolEnum.DelegateTask, resultContent: null);
        call.SubAgents = [.. branchStatuses.Select(status => new SubAgentMessage
        {
            Task = "do the thing",
            Status = status
        })];

        return call;
    }

    private static int[] MessageRowIndices(MessageWindowPlan plan)
        => [.. plan.Rows
            .Where(row => row.Kind == WindowRowKind.Message)
            .Select(row => row.Index)];

    #endregion

    #region Short history

    [Fact]
    public void Build_ShortHistory_RendersEveryMessageWithoutGroup()
    {
        // Arrange - 30 messages fit entirely into the tail window.
        var messages = BuildHistory(30);

        // Act
        var plan = MessageWindowPolicy.Build(messages);

        // Assert
        Assert.Equal(30, plan.Rows.Count);
        Assert.All(plan.Rows, row => Assert.Equal(WindowRowKind.Message, row.Kind));
        Assert.Equal(Enumerable.Range(0, 30), MessageRowIndices(plan));
        Assert.False(plan.HasGroup);
        Assert.Equal(0, plan.HiddenCount);
        Assert.Equal(new MessageWindowStats(0, 0, 0), plan.HiddenStats);
    }

    [Fact]
    public void Build_EmptyHistory_ProducesEmptyPlan()
    {
        // Act
        var plan = MessageWindowPolicy.Build([]);

        // Assert
        Assert.Empty(plan.Rows);
        Assert.False(plan.HasGroup);
        Assert.Equal(0, plan.HiddenCount);
        Assert.Empty(MessageWindowPolicy.MaterialisedIndices(plan));
    }

    #endregion

    #region Long history layout

    [Fact]
    public void Build_LongHistory_KeepsHeadThenGroupThenTail()
    {
        // Arrange
        var messages = BuildHistory(300);

        // Act
        var plan = MessageWindowPolicy.Build(messages);

        // Assert - the layout is exactly [head][group][tail].
        Assert.Equal(MessageWindowPolicy.HeadWindowCount + 1 + MessageWindowPolicy.TailWindowCount, plan.Rows.Count);

        for (var i = 0; i < MessageWindowPolicy.HeadWindowCount; i++)
        {
            var row = plan.Rows[i];
            Assert.Equal(WindowRowKind.Message, row.Kind);
            Assert.Equal(i, row.Index);
            Assert.Equal(1, row.Count);
        }

        var group = plan.Rows[MessageWindowPolicy.HeadWindowCount];
        Assert.Equal(WindowRowKind.Group, group.Kind);
        Assert.Equal(MessageWindowPolicy.HeadWindowCount, group.Index);
        Assert.Equal(260, group.Count);

        Assert.Equal(MessageWindowPolicy.HeadWindowCount, plan.HeadCount);
        Assert.Equal(270, plan.TailStart);
        Assert.Equal(260, plan.HiddenCount);
        Assert.True(plan.HasGroup);

        var expectedTail = Enumerable.Range(270, 30).ToArray();
        Assert.Equal(expectedTail, MessageRowIndices(plan).Skip(MessageWindowPolicy.HeadWindowCount));

        // The last row is the real last message, so IsLast keeps its meaning under the window.
        var lastRow = plan.Rows[^1];
        Assert.Equal(WindowRowKind.Message, lastRow.Kind);
        Assert.Equal(messages.Count - 1, lastRow.Index);
    }

    #endregion

    #region Hidden-range statistics

    [Fact]
    public void Build_HiddenStats_CountMessagesToolCallsAndTokensWithoutTechnicalNotes()
    {
        // Arrange - every message carries exactly 400 characters, which keeps the arithmetic exact.
        var messages = BuildHistory(300, content: new string('x', 400));

        // A real tool call inside the hidden range: 200 argument chars + 100 result chars.
        messages[20].ToolCalls = [CreateToolCall(arguments: new string('a', 200), resultContent: new string('r', 100))];

        // A technical note is never sent to the model, so it must not inflate the token figure -
        // and it is deliberately huge, so including it would be impossible to miss.
        var technical = messages[30];
        technical.IsTechnical = true;
        technical.Content = new string('t', 100_000);
        technical.ToolCalls = [CreateToolCall(arguments: new string('t', 5_000), resultContent: new string('t', 5_000))];

        // Act
        var plan = MessageWindowPolicy.Build(messages);

        // Assert - 260 hidden messages: 259 non-technical * 400 + 200 + 100 = 103 900 chars => /4.
        Assert.Equal(260, plan.HiddenStats.Messages);
        Assert.Equal(1, plan.HiddenStats.ToolCalls);
        Assert.Equal(103_900 / 4, plan.HiddenStats.ApproxTokens);
        Assert.True(plan.HiddenStats.ApproxTokens > 0);
    }

    [Fact]
    public void Build_NoHiddenRange_ReportsZeroStats()
    {
        // Arrange
        var messages = BuildHistory(30);

        // Act
        var plan = MessageWindowPolicy.Build(messages);

        // Assert
        Assert.Equal(0, plan.HiddenStats.Messages);
        Assert.Equal(0, plan.HiddenStats.ToolCalls);
        Assert.Equal(0, plan.HiddenStats.ApproxTokens);
    }

    #endregion

    #region MinHiddenCount

    [Fact]
    public void Build_BelowMinHiddenCount_ShowsEverything()
    {
        // Arrange - exactly one message short of the smallest fold worth a plaque.
        var messages = BuildHistory(MessageWindowPolicy.HeadWindowCount + MessageWindowPolicy.MinHiddenCount - 1);

        // Act
        var plan = MessageWindowPolicy.Build(messages);

        // Assert
        Assert.False(plan.HasGroup);
        Assert.Equal(0, plan.HiddenCount);
        Assert.Equal(messages.Count, plan.Rows.Count);
        Assert.Equal(Enumerable.Range(0, messages.Count), MessageRowIndices(plan));
    }

    [Fact]
    public void Build_OneMessageBelowTheFoldThreshold_ShowsEverything()
    {
        // Arrange - this is where the gate really bites: the tail window is already full, so the
        // only thing that keeps the fold away is MinHiddenCount (19 hidden rows are not worth a
        // plaque, 20 are).
        var messages = BuildHistory(
            MessageWindowPolicy.HeadWindowCount + MessageWindowPolicy.TailWindowCount + MessageWindowPolicy.MinHiddenCount - 1);

        // Act
        var plan = MessageWindowPolicy.Build(messages);

        // Assert
        Assert.False(plan.HasGroup);
        Assert.Equal(0, plan.HiddenCount);
        Assert.Equal(19, MessageWindowPolicy.DesiredTailStart(messages.Count, 0) - MessageWindowPolicy.HeadWindowCount);
        Assert.Equal(messages.Count, plan.Rows.Count);
    }

    [Fact]
    public void Build_AtMinHiddenCount_ShowsGroup()
    {
        // Arrange - the first size at which the tail window leaves MinHiddenCount behind.
        var total = MessageWindowPolicy.HeadWindowCount + MessageWindowPolicy.TailWindowCount + MessageWindowPolicy.MinHiddenCount;
        var messages = BuildHistory(total);

        // Act
        var plan = MessageWindowPolicy.Build(messages);

        // Assert
        Assert.True(plan.HasGroup);
        Assert.Equal(MessageWindowPolicy.MinHiddenCount, plan.HiddenCount);
        Assert.Equal(MessageWindowPolicy.HeadWindowCount + MessageWindowPolicy.MinHiddenCount, plan.TailStart);
    }

    #endregion

    #region Expanded pages

    [Fact]
    public void Build_ExpandedPages_RevealOnePageEachAndNeverHideMoreThanExists()
    {
        // Arrange
        var messages = BuildHistory(300);

        for (var pages = 0; pages <= 10; pages++)
        {
            // Act
            var plan = MessageWindowPolicy.Build(messages, pages);

            // Assert - one page reveals exactly GroupPageSize rows of the middle, never more.
            var expectedTailStart = Math.Max(
                MessageWindowPolicy.HeadWindowCount,
                messages.Count - (MessageWindowPolicy.TailWindowCount + pages * MessageWindowPolicy.GroupPageSize));

            Assert.Equal(expectedTailStart, plan.TailStart);
            Assert.Equal(expectedTailStart - MessageWindowPolicy.HeadWindowCount, plan.HiddenCount);
            Assert.True(plan.HiddenCount >= 0);
        }

        // Ten pages are more than the hidden range: the fold is gone, and nothing is hidden twice.
        var fullyRevealed = MessageWindowPolicy.Build(messages, expandedPages: 10);
        Assert.False(fullyRevealed.HasGroup);
        Assert.Equal(messages.Count, fullyRevealed.Rows.Count);
    }

    [Fact]
    public void Build_NegativeExpandedPages_BehavesLikeNoPages()
    {
        // Arrange
        var messages = BuildHistory(300);

        // Act
        var plan = MessageWindowPolicy.Build(messages, expandedPages: -5);

        // Assert
        Assert.Equal(MessageWindowPolicy.DesiredTailStart(300, 0), plan.TailStart);
        Assert.Equal(MessageWindowPolicy.Build(messages).HiddenCount, plan.HiddenCount);
    }

    #endregion

    #region Pin (hysteresis around a run)

    [Fact]
    public void Build_PinnedTailStartSmallerThanDesired_RevealsMore()
    {
        // Arrange
        var messages = BuildHistory(300);
        var unpinned = MessageWindowPolicy.Build(messages);
        Assert.Equal(270, unpinned.TailStart);

        // Act
        var plan = MessageWindowPolicy.Build(messages, pinnedTailStart: 200);

        // Assert - a pin can only ever reveal: the tail grew from 30 rows to 100 rows.
        Assert.Equal(200, plan.TailStart);
        Assert.Equal(190, plan.HiddenCount);
        Assert.True(plan.HiddenCount < unpinned.HiddenCount);
        Assert.True(plan.Rows.Count > unpinned.Rows.Count);
    }

    [Fact]
    public void Build_PinnedTailStartGreaterThanDesired_DoesNotHideMore()
    {
        // Arrange
        var messages = BuildHistory(300);

        // Act
        var plan = MessageWindowPolicy.Build(messages, pinnedTailStart: 290);

        // Assert - the pin is capped by the desired start, so a stale pin cannot hide anything.
        Assert.Equal(270, plan.TailStart);
        Assert.Equal(260, plan.HiddenCount);
    }

    [Fact]
    public void Build_PinnedTailStartBelowHead_IsClampedToHead()
    {
        // Arrange
        var messages = BuildHistory(300);

        // Act
        var plan = MessageWindowPolicy.Build(messages, pinnedTailStart: 3);

        // Assert - the head is always materialised; the clamp turns the pin into "whole history".
        Assert.Equal(MessageWindowPolicy.HeadWindowCount, plan.TailStart);
        Assert.Equal(0, plan.HiddenCount);
        Assert.False(plan.HasGroup);
        Assert.Equal(300, plan.Rows.Count);
    }

    #endregion

    #region Protected messages

    [Fact]
    public void Build_PendingApprovalInsideHiddenRange_CutsTheGroupBeforeIt()
    {
        // Arrange
        var messages = BuildHistory(300);
        messages[50].ToolCalls = [CreateToolCall(approval: ToolApprovalStatus.Pending)];

        // Act
        var plan = MessageWindowPolicy.Build(messages);

        // Assert - an approval gate behind the plaque would be an invisible hang.
        Assert.Equal(50, plan.TailStart);
        Assert.Equal(40, plan.HiddenCount);
        Assert.Equal(40, plan.Rows[MessageWindowPolicy.HeadWindowCount].Count);
        Assert.Contains(50, MessageRowIndices(plan));
    }

    [Fact]
    public void Build_PendingApprovalTooCloseToHead_DropsTheGroupEntirely()
    {
        // Arrange - cutting at index 15 leaves only 5 hidden messages, below MinHiddenCount.
        var messages = BuildHistory(300);
        messages[15].ToolCalls = [CreateToolCall(approval: ToolApprovalStatus.Pending)];

        // Act
        var plan = MessageWindowPolicy.Build(messages);

        // Assert
        Assert.False(plan.HasGroup);
        Assert.Equal(0, plan.HiddenCount);
        Assert.Equal(300, plan.Rows.Count);
        Assert.All(plan.Rows, row => Assert.Equal(WindowRowKind.Message, row.Kind));
    }

    [Fact]
    public void Build_StreamingMessage_IsProtected()
    {
        // Arrange
        var messages = BuildHistory(300);
        messages[100].IsStreaming = true;

        // Act
        var plan = MessageWindowPolicy.Build(messages);

        // Assert
        Assert.Equal(100, plan.TailStart);
        Assert.Equal(90, plan.HiddenCount);
        Assert.Contains(100, MessageRowIndices(plan));
    }

    [Fact]
    public void Build_RunningSubAgentBranch_IsProtected()
    {
        // Arrange - the branch renders inside the bubble; folding it would hide a live run.
        var messages = BuildHistory(300);
        messages[200].ToolCalls = [CreateDelegateTaskCall(SubAgentStatus.Running)];

        // Act
        var plan = MessageWindowPolicy.Build(messages);

        // Assert
        Assert.Equal(200, plan.TailStart);
        Assert.Contains(200, MessageRowIndices(plan));
    }

    [Theory]
    [InlineData(SubAgentStatus.Pending)]
    [InlineData(SubAgentStatus.NeedAction)]
    [InlineData(SubAgentStatus.Paused)]
    public void Build_UnfinishedSubAgentBranch_IsProtected(SubAgentStatus status)
    {
        // Arrange
        var messages = BuildHistory(300);
        messages[120].ToolCalls = [CreateDelegateTaskCall(status)];

        // Act
        var plan = MessageWindowPolicy.Build(messages);

        // Assert
        Assert.Equal(120, plan.TailStart);
    }

    [Theory]
    [InlineData(SubAgentStatus.Completed)]
    [InlineData(SubAgentStatus.Cancelled)]
    [InlineData(SubAgentStatus.Failed)]
    public void Build_FinishedSubAgentBranch_IsNotProtected(SubAgentStatus status)
    {
        // Arrange - a finished branch needs nothing from the user, so the fold may swallow it.
        var messages = BuildHistory(300);
        messages[120].ToolCalls = [CreateDelegateTaskCall(status)];

        // Act
        var plan = MessageWindowPolicy.Build(messages);

        // Assert
        Assert.Equal(270, plan.TailStart);
        Assert.DoesNotContain(120, MessageRowIndices(plan));
    }

    [Fact]
    public void Build_ExecutingTool_IsProtected()
    {
        // Arrange
        var call = CreateToolCall(resultContent: null);
        call.MarkExecutionStarted();

        var messages = BuildHistory(300);
        messages[150].ToolCalls = [call];

        // Act
        var plan = MessageWindowPolicy.Build(messages);

        // Assert
        Assert.Equal(150, plan.TailStart);
        Assert.Contains(150, MessageRowIndices(plan));
    }

    [Fact]
    public void Build_AskUserWithoutAnswer_IsProtected()
    {
        // Arrange - the answer widget lives in the block, and the run waits for it.
        var messages = BuildHistory(300);
        messages[160].ToolCalls = [CreateToolCall(name: BasicEnum.AskUser, resultContent: null)];

        // Act
        var plan = MessageWindowPolicy.Build(messages);

        // Assert
        Assert.Equal(160, plan.TailStart);
    }

    [Fact]
    public void Build_AnsweredAskUser_IsNotProtected()
    {
        // Arrange
        var messages = BuildHistory(300);
        messages[160].ToolCalls = [CreateToolCall(name: BasicEnum.AskUser, resultContent: "user said yes")];

        // Act
        var plan = MessageWindowPolicy.Build(messages);

        // Assert
        Assert.Equal(270, plan.TailStart);
    }

    [Fact]
    public void IsProtected_PlainMessage_IsFalse()
    {
        // Arrange
        var message = new VisualChatMessage { Content = "hello" };

        // Assert
        Assert.False(MessageWindowPolicy.IsProtected(message));
    }

    [Fact]
    public void Build_ProtectedMessageInsideTail_DoesNotMoveBoundary()
    {
        // Arrange - index 280 is already in the tail; protection must not shorten the window.
        var messages = BuildHistory(300);
        messages[280].ToolCalls = [CreateToolCall(approval: ToolApprovalStatus.Pending)];

        // Act
        var plan = MessageWindowPolicy.Build(messages);

        // Assert
        Assert.Equal(270, plan.TailStart);
        Assert.Equal(260, plan.HiddenCount);
    }

    #endregion

    #region Materialised indices

    [Fact]
    public void MaterialisedIndices_ReturnsExactlyTheMessageRows()
    {
        // Arrange
        var messages = BuildHistory(300);
        var plan = MessageWindowPolicy.Build(messages);

        // Act
        var indices = MessageWindowPolicy.MaterialisedIndices(plan).ToArray();

        // Assert - the group row contributes nothing: it is the plaque, not a message.
        Assert.Equal(Enumerable.Range(0, 10).Concat(Enumerable.Range(270, 30)), indices);
        Assert.Equal(plan.Rows.Count(row => row.Kind == WindowRowKind.Message), indices.Length);
        Assert.DoesNotContain(10, indices);
        Assert.DoesNotContain(269, indices);
    }

    #endregion
}

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace UIBlazor.Services.AgentFramework;

/// <summary>
/// The MAIN agent's LLM gateway: a Microsoft.Extensions.AI <see cref="FunctionInvokingChatClient"/>
/// run that carries the model ↔ tool round-trips in the chat-client pipeline
/// (MaximumIterationsPerRequest caps sub-agent runs). Tool execution routes back through
/// <see cref="IToolCallHandler"/>, so the approval flow (category rules, ask_user, parallel
/// delegate_task, ApprovalRequired UI events) stays identical to the legacy loop. App-level
/// features (todo / mode / skills / web / file memory / compaction) stay ours - the
/// harness-provided variants were never wired in. Sub-agent branches (AfSubAgentOrchestrator)
/// reuse this loop with MaximumIterations set; the single-shot adapter
/// (<see cref="AfCompletionsAdapter"/>) remains for context compression.
/// </summary>
internal sealed class AfAgentLoop(
    Func<ConnectionProfile> profile,
    HttpClient httpClient,
    IToolCallHandler toolCallHandler,
    Func<ConnectionProfile, HttpClient, IChatClient>? chatClientFactory = null) : ICompletionsGateway
{
    private readonly Func<ConnectionProfile, HttpClient, IChatClient> _chatClientFactory =
        chatClientFactory ?? AfChatClientFactory.Create;

    /// <summary>
    /// Function-invocation iteration cap for this run (MaximumIterationsPerRequest).
    /// Null = stack default (40). Sub-agent loops set it from profile.MaxIterationsPerSubAgent;
    /// the main loop stays on the default.
    /// </summary>
    public int? MaximumIterations { get; init; }

    public async IAsyncEnumerable<ChatDelta> GetCompletionsAsync(
        IReadOnlyList<WireMessage> messages,
        IEnumerable<Tool>? tools,
        ConversationSession targetSession,
        CompletionsResult resultCapture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var currentProfile = profile();
        var chatMessages = AfMessageMapper.Map(messages);
        var chatOptions = AfChatClientFactory.BuildChatOptions(
            currentProfile,
            AfToolMapper.Map(
                tools ?? [],
                (name, argsJson, ct) => ExecuteToolForLoopAsync(name, argsJson, targetSession, resultCapture, ct)));

        // Pipeline: raw OpenAI client → FunctionInvokingChatClient (the model ↔ tool loop,
        // executing each tool call via ExecuteToolForLoopAsync). The constructor argument is
        // disposed through the outer client (DelegatingChatClient disposes the inner), so the
        // raw instance gets NO separate `using` - avoid double dispose.
        using var invocationClient = new FunctionInvokingChatClient(_chatClientFactory(currentProfile, httpClient))
        {
            AllowConcurrentInvocation = false
        };

        // Null = leave the stack default (40), preserving the previous null-passing semantics.
        if (MaximumIterations is { } maximumIterations)
        {
            invocationClient.MaximumIterationsPerRequest = maximumIterations;
        }

        // NOTE: no run-scoped timeout CTS. HttpTimeoutSec is enforced per HTTP request by the
        // transport handler (DI HttpClient); a run-scoped timeout would kill long tools
        // (sub-agents). AfCancelRace.GuardAsync races a stuck transport read against the user
        // token and classifies any non-user OperationCanceledException (per-request HttpClient
        // timeout) as "Request timed out".
        // Register the cancel-race BEFORE any linked CTS on this token: token callbacks run
        // in registration order, and linkedCts' callback unwinding an in-flight tool wait
        // (~100ms) would otherwise let the FICC MoveNext complete first, so the race loses
        // and cancellation only surfaces at the next consumer pull (+~750ms).
        using var race = AfCancelRace.Build(cancellationToken);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var updates = AfCancelRace.GuardAsync(
            invocationClient.GetStreamingResponseAsync(chatMessages, chatOptions, linkedCts.Token),
            () => "Request timed out",
            cancellationToken,
            race);

        // Pre-run context base: PumpAsync rebases token estimation from THIS value on every
        // delta (visible tokens accumulate inside resultCapture; using an updated value would
        // double-count previously estimated tokens).
        await foreach (var delta in AfDeltaMapper.PumpAsync(updates, targetSession, resultCapture, cancellationToken))
        {
            yield return delta;
        }
    }

    /// <summary>
    /// Executes one model-requested tool call through the full legacy approval path
    /// (PrepareToolsForApprovals → ApprovalRequired/waiters → ExecuteToolAsync).
    /// Execution failures return error text to the model so the loop keeps iterating -
    /// re-running the whole run would re-execute approved tools.
    /// Returns the tool result text for the model continuation.
    /// </summary>
    private async Task<string> ExecuteToolForLoopAsync(
        string name,
        string argsJson,
        ConversationSession targetSession,
        CompletionsResult resultCapture,
        CancellationToken cancellationToken)
    {
        // Bind to the UI-rendered accumulated ToolCall (same tool name, not executed yet) so that
        // ApprovalStatus / Result updates are visible on screen; fall back to a detached object
        // if execution raced ahead of delta processing.
        var bound = resultCapture.AccumulatedToolCalls?
            .FirstOrDefault(tc => tc.Function.Name == name && tc.Result is null);

        // CallId is not used by the model here, only to bind toolCall results and approvals.
        var toolCall = bound ?? new ToolCall
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "function",
            Function = new ToolCallFunction { Name = name, Arguments = argsJson },
            IsReady = true,
            ApprovalStatus = ToolApprovalStatus.Pending
        };

        try
        {
            // Render boundary: yield so Blazor can flush the pending StateHasChanged from the
            // tool-call delta before we execute. Without it an auto-approved tool (e.g.
            // delegate_task) starts before its block/view is rendered and subscribed, so the
            // sub-agent's own approvals never surface in the UI (legacy razor loop had this
            // boundary via `await InvokeAsync(StateHasChanged)` before ProcessToolCallsAsync).
            await Task.Yield();

            toolCallHandler.PrepareToolsForApprovals([toolCall]);
            await toolCallHandler.ProcessToolCallsAsync([toolCall], targetSession.Mode, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Tool {name} execution failed: {ex.Message}";
        }

        // At least one tool was executed inside the agent loop: the UI must not re-process it.
        resultCapture.ToolsExecutedByAgent = true;

        return toolCall.Result?.Content ?? "(tool returned no output)";
    }
}

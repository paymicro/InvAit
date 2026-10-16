using System.Collections.Concurrent;
using Shared.Contracts.Mcp;

namespace UIBlazor.Services;

public class ToolCallHandler(IToolManager toolManager) : IToolCallHandler
{
    internal readonly ConcurrentDictionary<string, ApprovalGate> _approvalGates = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _askUserWaiters = new();
    private BashCommandClassifier? _bashClassifier;

    /// <summary>
    /// Лениво создаёт классификатор bash-команд на основе текущих настроек.
    /// </summary>
    private BashCommandClassifier GetBashClassifier()
    {
        var settings = toolManager.Current?.BashSettings ?? new BashCommandSettings();
        if (_bashClassifier is null || _bashClassifier.SettingsHash != ComputeSettingsHash(settings))
        {
            _bashClassifier = new BashCommandClassifier(settings);
            _bashClassifier.SettingsHash = ComputeSettingsHash(settings);
        }
        return _bashClassifier;
    }

    private static string ComputeSettingsHash(BashCommandSettings settings)
    {
        // Use control chars as separators to avoid collision with comma-containing regex patterns
        return $"{string.Join("\x1f", settings.AllowPatterns ?? [])}\x1e{string.Join("\x1f", settings.DenyPatterns ?? [])}";
    }

    /// <summary>
    /// Определяет approval mode для bash-команды.
    /// <para>
    /// Классификатор всегда активен и определяет тип команды
    /// (Safe/Unknown/Destructive/UserDenied).
    /// Category mode (Allow/Ask/Deny) задаёт «потолок доверия».
    /// </para>
    /// <list type="table">
    /// <listheader>
    /// <term>Category</term><term>Safe</term><term>Unknown</term>
    /// <term>Destructive</term><term>UserDenied</term>
    /// </listheader>
    /// <item><term>Allow</term><term>Allow</term><term>Allow</term><term>Ask</term><term>Deny</term></item>
    /// <item><term>Ask</term><term>Allow</term><term>Ask</term><term>Ask</term><term>Deny</term></item>
    /// <item><term>Deny</term><term>Deny</term><term>Deny</term><term>Deny</term><term>Deny</term></item>
    /// </list>
    /// <para>
    /// UserDenied (пользовательские deny-паттерны) → всегда Deny (авто-отклонение).
    /// Destructive (встроенные паттерны) → Ask (подтверждение, но не отклонение).
    /// </para>
    /// </summary>
    private ToolApprovalMode GetBashApprovalMode(string argumentsJson)
    {
        var categoryMode = toolManager.GetApprovalModeByToolName(BuiltInToolEnum.Bash);

        // Category Deny — everything denied, classifier irrelevant
        if (categoryMode == ToolApprovalMode.Deny)
            return ToolApprovalMode.Deny;

        var command = ExtractCommand(argumentsJson);
        var classifier = GetBashClassifier();
        var classification = classifier.Classify(command);

        // User-specified deny patterns → always auto-reject, regardless of category
        if (classification == BashCommandClassification.UserDenied)
            return ToolApprovalMode.Deny;

        // Map classification → approval mode based on category
        return (categoryMode, classification) switch
        {
            // Category Allow: trust more — safe + unknown auto-execute, destructive asks
            (ToolApprovalMode.Allow, BashCommandClassification.Safe)        => ToolApprovalMode.Allow,
            (ToolApprovalMode.Allow, BashCommandClassification.Unknown)     => ToolApprovalMode.Allow,
            (ToolApprovalMode.Allow, BashCommandClassification.Destructive) => ToolApprovalMode.Ask,

            // Category Ask: safe auto-execute, unknown + destructive ask
            (ToolApprovalMode.Ask, BashCommandClassification.Safe)          => ToolApprovalMode.Allow,
            (ToolApprovalMode.Ask, BashCommandClassification.Unknown)       => ToolApprovalMode.Ask,
            (ToolApprovalMode.Ask, BashCommandClassification.Destructive)   => ToolApprovalMode.Ask,

            // Deny already handled above, UserDenied already handled above
            _ => ToolApprovalMode.Ask,
        };
    }

    private static string? ExtractCommand(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        try
        {
            var args = JsonUtils.DeserializeParameters(argumentsJson);
            return args.GetString("command");
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public event Action<string>? ApprovalRequired;

    public void PrepareToolsForApprovals(List<ToolCall> toolCalls)
    {
        // Evict "early" approvals (decisions delivered while no waiter consumed them) that
        // expired, so stale clicks do not accumulate in memory over a long-lived session.
        // Must run before CancelPendingApprovals, which clears everything else.
        var now = DateTime.UtcNow;
        foreach (var kvp in _approvalGates)
        {
            if (kvp.Value.Decision is not null && now - kvp.Value.CreatedAtUtc > ApprovalGate.EarlyApprovalTtl)
                _approvalGates.TryRemove(kvp.Key, out _);
        }

        CancelPendingApprovals();

        string? firstPendingId = null;

        // Pre-register approval waiters for all pending segments
        // so users can approve tools in any order
        foreach (var toolCall in toolCalls)
        {
            toolCall.IsReady = true;
            toolCall.Function.Arguments = CleanJsonArguments(toolCall.Function.Arguments);

            // AskUser always requires user interaction (wait for answer)
            if (toolCall.Function.Name == BasicEnum.AskUser)
            {
                toolCall.ApprovalStatus = ToolApprovalStatus.Pending;
                _askUserWaiters[toolCall.Id] = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                firstPendingId ??= toolCall.Id;
                continue;
            }

            var approvalMode = toolCall.Function.Name == BuiltInToolEnum.Bash
                ? GetBashApprovalMode(toolCall.Function.Arguments)
                : toolManager.GetApprovalModeByToolName(toolCall.Function.Name);
            switch (approvalMode)
            {
                case ToolApprovalMode.Ask:
                    toolCall.ApprovalStatus = ToolApprovalStatus.Pending;
                    // Reuse an already-present gate (e.g. a decision raced in before the
                    // registration) so an early user decision survives into the new batch.
                    _approvalGates.GetOrAdd(toolCall.Id, _ => new ApprovalGate(toolCall));
                    firstPendingId ??= toolCall.Id;
                    break;
                case ToolApprovalMode.Deny:
                    toolCall.ApprovalStatus = ToolApprovalStatus.Rejected;
                    break;
                case ToolApprovalMode.Allow:
                default:
                    toolCall.ApprovalStatus = ToolApprovalStatus.Approved;
                    break;
            }
        }

        // Notify subscribers that user action is required
        if (firstPendingId is not null)
        {
            ApprovalRequired?.Invoke(firstPendingId);
        }
    }

    private static bool ToolCanExecute(Tool? tool, AppMode appMode)
        => tool is { Enabled: true } && ToolManager.EnableInMode(tool, appMode);

    public async Task ProcessToolCallsAsync(
        List<ToolCall> toolCalls,
        AppMode appMode,
        CancellationToken cancellationToken)
    {
        // Separate delegate_task calls (can run in parallel) from other tool calls (sequential).
        // Each sub-agent has its own ToolCallHandler, so approvals are isolated.
        // Non-delegate tools must remain sequential because they share the same approval waiters.
        var delegateTasks = new List<(ToolCall toolCall, Tool? tool)>();
        var sequentialTasks = new List<(ToolCall toolCall, Tool? tool)>();

        foreach (var toolCall in toolCalls)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            var tool = toolManager.GetTool(toolCall.Function.Name);

            if (!ToolCanExecute(tool, appMode))
            {
                toolCall.Result = new ToolResult
                {
                    Content = string.Join(" ",
                        new string[] { "Tool", tool?.Name ?? string.Empty, "can't execute" }.Where(s => !string.IsNullOrEmpty(s))),
                    Name = toolCall.Function.Name,
                    DisplayName = tool?.DisplayName ?? string.Empty,
                    Success = false
                };
                continue;
            }

            if (toolCall.Function.Name == BuiltInToolEnum.DelegateTask)
                delegateTasks.Add((toolCall, tool));
            else
                sequentialTasks.Add((toolCall, tool));
        }

        // Run delegate_task calls in parallel
        if (delegateTasks.Count > 0)
        {
            var parallelTasks = delegateTasks.Select(async item =>
            {
                var vsToolResult = await ExecuteToolWithApprovalAsync(item.toolCall, item.tool, cancellationToken);
#if DEBUG
                vsToolResult = HeadlessMocker.GetVsToolResult(vsToolResult);
#endif
                item.toolCall.Result = ToolResult.Convert(vsToolResult, item.tool?.DisplayName ?? "", item.tool?.Name ?? "");
            });

            await Task.WhenAll(parallelTasks);
        }

        // Run other tool calls sequentially
        foreach (var (toolCall, tool) in sequentialTasks)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            var vsToolResult = await ExecuteToolWithApprovalAsync(toolCall, tool, cancellationToken);

#if DEBUG
            vsToolResult = HeadlessMocker.GetVsToolResult(vsToolResult);
#endif

            toolCall.Result = ToolResult.Convert(vsToolResult, tool?.DisplayName ?? "", tool?.Name ?? "");
        }
    }

    private async Task<VsToolResult> ExecuteToolWithApprovalAsync(
        ToolCall toolCall,
        Tool? tool,
        CancellationToken cancellationToken)
    {
        if (tool == null)
        {
            return VsToolResult.Failed(toolCall.Function.Name, "Tool not found.");
        }

        // AskUser: wait for user to select an option or type a custom answer
        if (toolCall.Function.Name == BasicEnum.AskUser && _askUserWaiters.TryGetValue(toolCall.Id, out var askTcs))
        {
            try
            {
                var answer = await askTcs.Task.WaitAsync(cancellationToken);
                _askUserWaiters.TryRemove(toolCall.Id, out _);
                toolCall.ApprovalStatus = ToolApprovalStatus.Approved;
                return new VsToolResult
                {
                    Name = BasicEnum.AskUser,
                    Success = true,
                    Result = answer
                };
            }
            catch (OperationCanceledException)
            {
                _askUserWaiters.TryRemove(toolCall.Id, out _);
                return VsToolResult.Cancelled(toolCall.Function.Name);
            }
        }

        if (toolCall.ApprovalStatus == ToolApprovalStatus.Pending)
        {
            toolCall.ApprovalStatus = await WaitForApprovalAsync(toolCall, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return VsToolResult.Cancelled(toolCall.Function.Name);
            }
        }

        return await ExecuteToolAsync(tool, toolCall, cancellationToken);
    }

    private async Task<ToolApprovalStatus> WaitForApprovalAsync(
        ToolCall toolCall,
        CancellationToken cancellationToken)
    {
        // The gate is the single point of truth for the decision: HandleApprovalAsync
        // records the decision (and releases the signal) on the same object this wait
        // observes, whether it arrives before or after the wait starts — so a decision
        // can never be lost or double-applied.
        var gate = _approvalGates.GetOrAdd(toolCall.Id, _ => new ApprovalGate(toolCall));

        try
        {
            await gate.Signal.WaitAsync(cancellationToken);

            var decision = gate.Decision!.Value;
            toolCall.ApprovalStatus = decision;
            return decision;
        }
        catch (OperationCanceledException)
        {
            // Cancellation (e.g. a timeout watchdog) may race with a decision that was
            // just recorded on this gate — the recorded decision wins over cancellation,
            // otherwise an approved tool would surface as Rejected.
            CancelPendingApprovals();
            return gate.Decision ?? ToolApprovalStatus.Rejected;
        }
        finally
        {
            _approvalGates.TryRemove(toolCall.Id, out _);
        }
    }

    private static string CleanJsonArguments(string rawArguments)
    {
        if (string.IsNullOrWhiteSpace(rawArguments))
            return "{}";

        var trimmed = rawArguments.Trim();

        // Находим первый символ '{' и ПОСЛЕДНИЙ символ '}'
        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');

        // Если скобки найдены и они расположены правильно
        if (firstBrace != -1 && lastBrace > firstBrace)
        {
            // Вырезаем только то, что находится внутри объекта включительно
            return trimmed.Substring(firstBrace, lastBrace - firstBrace + 1);
        }

        return "{}"; // Фолбэк, если структура вообще нарушена
    }

    private static async Task<VsToolResult> ExecuteToolAsync(
        Tool tool,
        ToolCall toolCall,
        CancellationToken cancellationToken)
    {
        if (toolCall.ApprovalStatus != ToolApprovalStatus.Approved)
        {
            return VsToolResult.Denied(toolCall.Function.Name);
        }

        // Use context-aware execution if available (for delegate_task)
        if (tool.ExecuteWithContextAsync is not null)
        {
            return await tool.ExecuteWithContextAsync(toolCall.Function.Arguments, toolCall, cancellationToken);
        }

        var result = await tool.ExecuteAsync(toolCall.Function.Arguments, cancellationToken);

        if (tool.Category != ToolCategory.Mcp)
        {
            // Для MCP добавлено распаковка контента MCPToolResult
            return result;
        }

        var mcpToolResult = JsonUtils.Deserialize<MCPToolResult>(result.Result);
        if (mcpToolResult is null)
        {
            return result;
        }

        var sb = new StringBuilder();
        foreach (var content in mcpToolResult.Content)
        {
            sb.AppendLine(
                content.Type switch
                {
                    "text" => content.Text,
                    "image" => $"## Image\n{content.Data}\n##MimeType\n{content.MimeType}",
                    "resource" => $"## Resource\n{(content.Resource != null ? JsonUtils.Serialize(content.Resource) : null)}",
                    _ => "## Error - undefined content type"
                }
            );
        }
        var success = result.Success && !mcpToolResult.IsError;
        return new VsToolResult()
        {
            Name = result.Name,
            Success = success,
            Result = success ? sb.ToString() : string.Empty,
            ErrorMessage = !success ? sb.ToString() : string.Empty
        };
    }

    public Task HandleApprovalAsync(string toolCallId, bool approved)
    {
        var status = approved ? ToolApprovalStatus.Approved : ToolApprovalStatus.Rejected;

        // The decision always lands on the same gate object a (possibly later) wait will
        // observe; TryDecide makes a repeated decision for the same id a no-op. If no
        // waiter exists yet (the tool's execution chain has not reached its approval
        // wait), the gate keeps the decision until it is consumed or expires via TTL.
        var gate = _approvalGates.GetOrAdd(toolCallId, _ => new ApprovalGate());
        gate.TryDecide(status);

        return Task.CompletedTask;
    }

    public Task HandleAskUserAnswerAsync(string toolCallId, string answer)
    {
        if (_askUserWaiters.TryGetValue(toolCallId, out var tcs))
        {
            tcs.TrySetResult(answer);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void CancelPendingApprovals()
    {
        foreach (var kvp in _approvalGates)
        {
            // Gates still awaiting a decision die with their waits (TryDecide stamps
            // Rejected and releases the waiter). Gates that already carry a decision
            // ("early" approvals with no consumer yet) are kept: their lifetime is
            // governed by the TTL sweep in PrepareToolsForApprovals, and a decision
            // recorded while cancellation was in flight must win over the cancel.
            if (kvp.Value.TryDecide(ToolApprovalStatus.Rejected))
                _approvalGates.TryRemove(kvp.Key, out _);
        }

        foreach (var kvp in _askUserWaiters)
        {
            kvp.Value.TrySetCanceled();
        }
        _askUserWaiters.Clear();
    }
}

/// <summary>
/// Per-tool-call approval state: a decision plus a signal that a decision has been made.
/// TryDecide is idempotent; the decision itself is written under the gate's lock while
/// <see cref="Signal"/> (released exactly once) wakes up any waiter.
/// </summary>
internal sealed class ApprovalGate(ToolCall? toolCall = null)
{
    public static readonly TimeSpan EarlyApprovalTtl = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private ToolApprovalStatus? _decision;

    /// <summary>Tool call this gate belongs to; null for gates created by a decision with no registered tool call.</summary>
    public ToolCall? ToolCall => toolCall;

    /// <summary>Created to allow orphaned "early" decisions to be evicted after <see cref="EarlyApprovalTtl"/>.</summary>
    public DateTime CreatedAtUtc { get; internal set; } = DateTime.UtcNow;

    public SemaphoreSlim Signal { get; } = new(0, 1);

    public ToolApprovalStatus? Decision
    {
        get
        {
            lock (_lock)
            {
                return _decision;
            }
        }
    }

    /// <summary>Records the decision (first one only) and releases the signal.</summary>
    public bool TryDecide(ToolApprovalStatus status)
    {
        lock (_lock)
        {
            if (_decision is not null)
                return false;

            _decision = status;
        }

        toolCall?.ApprovalStatus = status;
        Signal.Release();
        return true;
    }
}

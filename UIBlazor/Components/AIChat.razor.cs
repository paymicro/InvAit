using System.ComponentModel;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Radzen;
using UIBlazor.Services;
using ConversationSession = UIBlazor.Models.ConversationSession;

namespace UIBlazor.Components;

public partial class AiChat : RadzenComponent
{
    private static readonly Regex PlanRegex = new(
        @"<plan>(?<plan>.*?)</plan>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(200));

    private List<VisualChatMessage> Messages => ChatService.Session.Messages;

    private bool IsLoading { get; set; }

    private DotNetObjectReference<AiChat>? _dotNetRef;

    private CancellationTokenSource _cts = new();

    private bool _callSettings;

    [Inject] private NotificationService NotificationService { get; set; } = null!;
    [Inject] private DialogService DialogService { get; set; } = null!;
    [Inject] private IChatService ChatService { get; set; } = null!;
    [Inject] private IToolManager ToolManager { get; set; } = null!;
    [Inject] private IProfileManager ProfileManager { get; set; } = null!;
    [Inject] private ICommonSettingsProvider CommonSettingsProvider { get; set; } = null!;
    [Inject] private IVsBridge VsBridge { get; set; } = null!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = null!;
    [Inject] private IMessageParser MessageParser { get; set; } = null!;
    [Inject] private ILogger<AiChat> Logger { get; set; } = null!;
    [Inject] private IRetryHandler RetryHandler { get; set; } = null!;
    [Inject] private IToolCallHandler ToolCallHandler { get; set; } = null!;
    [Inject] private ISubAgentExecutor SubAgentExecutor { get; set; } = null!;

    public async Task NewSessionAsync()
    {
        await ChatService.NewSessionAsync();
        await InvokeAsync(StateHasChanged);
    }

    public async Task SendMessageAsync(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || IsLoading)
            return;

        var userMessage = new VisualChatMessage
        {
            Content = content,
            Role = ChatMessageRole.User,
            IsExpanded = IsShortMessage(content)
        };

        ChatService.Session.AddMessage(userMessage);
        await ScrollToBottomAsync();
        await GetAiResponseAsync();
    }

    public async Task HandleCommandAsync(string command)
    {
        if (IsLoading) return;

        switch (command)
        {
            case "compact":
                await CancelResponseAsync();
                _cts = new CancellationTokenSource();
                IsLoading = true;
                await InvokeAsync(StateHasChanged);
                try
                {
                    await CompressAsync(0, _cts.Token);
                }
                finally
                {
                    IsLoading = false;
                    await InvokeAsync(StateHasChanged);
                }
                break;
        }
    }

    private async Task ScrollToBottomAsync()
    {
        await Task.Yield();
        await JsRuntime.InvokeVoidAsync("scrollToAnchor");
    }

    private async Task GetAiResponseAsync()
    {
        await CancelResponseAsync();
        _cts = new CancellationTokenSource();
        await GetAiResponseInternalAsync(0, _cts.Token);
    }

    /// <summary>
    /// Сжатие сессии. Полностью прозрачный для пользователя процесс.
    /// Выглядит как еще один промежуточный запрос и реорганизация сообщений.
    /// </summary>
    /// <param name="retryCount">Количество повторов</param>
    /// <returns>Сжалась ли сессия. False если завершилось ошибкой</returns>
    private async Task<bool> CompressAsync(int retryCount, CancellationToken cancellationToken)
    {
        var assistantMessage = CreateStreamingMessage("## ♻ \n\n");
        MessageParser.UpdateSegments(assistantMessage.Content, assistantMessage);
        ChatService.Session.AddMessage(assistantMessage);
        await InvokeAsync(StateHasChanged);

        var result = false;

        var completions = new CompletionsResult();

        try
        {
            await ChatService.ProcessStreamAsync(
                 assistantMessage,
                 ChatService.CompressSessionAsync(completions, cancellationToken),
                 onContentUpdate: content => MessageParser.UpdateSegments(content, assistantMessage),
                 onToolCallsUpdate: toolCalls =>
                 {
                     assistantMessage.ToolCalls = toolCalls;
                     assistantMessage.IsShouldRender = true;
                 },
                 onStateChange: () =>
                 {
                     assistantMessage.Model ??= completions.Model;
                     InvokeAsync(StateHasChanged);
                 },
                 completions,
                 cancellationToken);
            // обновление потерянных сегментов
            foreach (var message in ChatService.Session.Messages.Where(m => m.Segments.Count == 0))
            {
                MessageParser.UpdateSegments(message.Content, message);
            }
            result = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            HandleCancellation(assistantMessage);
        }
        catch (Exception ex)
        {
            await HandleErrorAsync(assistantMessage, ex, ++retryCount,
                async () =>
                {
                    result = await CompressAsync(retryCount, cancellationToken);
                },
                cancellationToken);
        }
        finally
        {
            assistantMessage.IsStreaming = false;
            await InvokeAsync(StateHasChanged);
        }

        return result;
    }

    private async Task GetAiResponseInternalAsync(int retryCount, CancellationToken cancellationToken)
    {
        IsLoading = true;

        if (ChatService.NeedCompression)
        {
            var result = await CompressAsync(0, cancellationToken);
            if (!result || cancellationToken.IsCancellationRequested)
            {
                IsLoading = false;
                return;
            }
        }

        var assistantMessage = CreateStreamingMessage();
        ChatService.Session.AddMessage(assistantMessage);
        await ChatService.SaveSessionAsync();
        await InvokeAsync(StateHasChanged);

        var completions = new CompletionsResult();

        try
        {
            await ChatService.ProcessStreamAsync(
                assistantMessage,
                ChatService.GetCompletionsAsync(completions, cancellationToken),
                onContentUpdate: content => MessageParser.UpdateSegments(content, assistantMessage),
                onToolCallsUpdate: toolCalls =>
                {
                    assistantMessage.ToolCalls = toolCalls;
                    assistantMessage.IsShouldRender = true;
                },
                onStateChange: () =>
                {
                    assistantMessage.Model ??= completions.Model;
                    InvokeAsync(StateHasChanged);
                },
                completions,
                cancellationToken);
            await HandleStreamCompletionAsync(assistantMessage, completions, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            HandleCancellation(assistantMessage);
        }
        catch (Exception ex)
        {
            await HandleErrorAsync(assistantMessage, ex, ++retryCount,
                async () => await GetAiResponseInternalAsync(retryCount, cancellationToken),
                cancellationToken);
        }
        finally
        {
            assistantMessage.IsStreaming = false;
            IsLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task HandleStreamCompletionAsync(VisualChatMessage message, CompletionsResult result, CancellationToken cancellationToken)
    {
        NotifyIfNeeded(result);
        ParsePlan(message);
        await ChatService.SaveSessionAsync();

        // Handle native tool_calls from the API response
        message.ToolCalls = result.AccumulatedToolCalls;
        if (message.ToolCalls is { Count: > 0 })
        {
            ToolCallHandler.PrepareToolsForApprovals(message.ToolCalls);
            message.IsShouldRender = true;
            await InvokeAsync(StateHasChanged);
            await ToolCallHandler.ProcessToolCallsAsync(message.ToolCalls, cancellationToken);
            ChatService.Session.TotalTokens += message.ToolCalls?.Sum(t => t.Tokens) ?? 0;
            await ChatService.SaveSessionAsync();
            message.IsShouldRender = true;
            await InvokeAsync(StateHasChanged);

            if (!cancellationToken.IsCancellationRequested)
            {
                await GetAiResponseAsync();
            }
            return;
        }
    }

    private void NotifyIfNeeded(CompletionsResult result)
    {
        if (result.FinishReason?.Equals("length", StringComparison.OrdinalIgnoreCase) == true)
        {
            NotificationService.Notify(new NotificationMessage
            {
                Severity = NotificationSeverity.Error,
                Summary = SharedResource.ErrorFinishByLength,
                Detail = string.Empty,
                Duration = 30_000,
                ShowProgress = true,
            });
        }

        if (!string.IsNullOrEmpty(result.Error))
        {
            NotificationService.Notify(new NotificationMessage
            {
                Severity = NotificationSeverity.Error,
                Summary = result.Error,
                Detail = string.Empty,
                Duration = 30_000,
                ShowProgress = true,
            });
        }
    }

    private void HandleCancellation(VisualChatMessage message)
    {
        if (string.IsNullOrEmpty(message.Content))
        {
            message.Content = "Cancelled by user...";
            MessageParser.UpdateSegments(message.Content, message);
        }

        if (message.ToolCalls is { Count: > 0 })
        {
            foreach (var toolCall in message.ToolCalls)
            {
                toolCall.IsReady = true;
                toolCall.ApprovalStatus = ToolApprovalStatus.Rejected;
            }
        }
    }

    private async Task HandleErrorAsync(VisualChatMessage message, Exception ex,
        int retryCount, Func<Task> retryAction, CancellationToken cancellationToken)
    {
        var maxRetries = CommonSettingsProvider.Current.MaxRetries;
        message.Content = $"Error: {ex.Message} [{retryCount}/{maxRetries}]";
        MessageParser.UpdateSegments(message.Content, message);
        Logger.LogError(ex, "Getting response error");

        NotificationService.Notify(new NotificationMessage
        {
            Severity = NotificationSeverity.Error,
            Summary = $"[{retryCount}/{maxRetries}] Response error",
            Detail = ex.Message,
            Duration = 30_000,
            ShowProgress = true,
        });

        if (retryCount >= maxRetries)
        {
            return;
        }

        var delay = RetryHandler.GetRetryDelay(retryCount);

        message.MaxRetryAttempts = maxRetries;
        message.RetryAttempt = retryCount;

        try
        {
            await RetryHandler.WaitForRetryAsync(delay, i =>
            {
                message.RetryCountdown = i;
                InvokeAsync(StateHasChanged);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            message.RetryCountdown = 0;
        }

        ChatService.Session.RemoveMessage(message.Id);
        await retryAction.Invoke();
    }

    private static VisualChatMessage CreateStreamingMessage(string initialContent = "") => new()
    {
        Role = ChatMessageRole.Assistant,
        IsStreaming = true,
        IsExpanded = true,
        Content = initialContent
    };

    private static void ParsePlan(VisualChatMessage message)
    {
        if (string.IsNullOrEmpty(message.Content)) return;

        var match = PlanRegex.Match(message.Content);
        if (match.Success)
        {
            message.PlanContent = match.Groups["plan"].Value.Trim();
            message.DisplayContent = PlanRegex.Replace(message.DisplayContent ?? message.Content, string.Empty).Trim();

            if (string.IsNullOrEmpty(message.DisplayContent))
            {
                message.DisplayContent = "Proposed Plan:";
            }
        }
    }

    private async Task ExecutePlanAsync(VisualChatMessage message)
    {
        if (!message.HasPlan) return;

        ChatService.Session.Mode = AppMode.Agent;
        await SendMessageAsync("Implement the plan.");
    }

    private Task HandleToolApprovalAsync((string MessageId, string SegmentId, bool Approved) args)
    {
        return ToolCallHandler.HandleApprovalAsync(args.SegmentId, args.Approved);
    }

    private void LoadMessagesFromSession()
    {
        foreach (var chatMessage in Messages)
        {
            if (chatMessage.Role == ChatMessageRole.Assistant)
            {
                ParsePlan(chatMessage);
            }

            chatMessage.IsExpanded = IsShortMessage(chatMessage.DisplayContent ?? chatMessage.Content);
            MessageParser.UpdateSegments(chatMessage.Content, chatMessage, isHistory: true);

            if (chatMessage.ToolCalls is not null)
            {
                foreach (var toolCall in chatMessage.ToolCalls.Where(tc => tc.Result is not null))
                {
                    var result = toolCall.Result;
                    result.DisplayName = ToolResult.GetDisplayName(
                        result.Success, ToolManager.GetTool(result.Name)?.DisplayName ?? result.Name);
                }
            }
        }

        InvokeAsync(StateHasChanged);
    }

    private static bool IsShortMessage(string content)
        => string.IsNullOrEmpty(content) || (content.Length < 1000 && content.Count(c => c == '\n') < 15);

    private async Task CancelResponseAsync() => await _cts.CancelAsync();

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();

        _dotNetRef = DotNetObjectReference.Create(this);
        ChatService.SessionChanged += HandleSessionChanged;
        SubAgentExecutor.SubAgentStateChanged += OnSubAgentStateChanged;
        ToolCallHandler.ApprovalRequired += OnApprovalRequired;

        ToolManager.RegisterAllTools();
        await VsBridge.InitializeAsync();
        await InvokeAsync(StateHasChanged);
    }

    private void HandleSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConversationSession))
            return;

        LoadMessagesFromSession();
        InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Called when a sub-agent's state changes during execution.
    /// Triggers Blazor re-render so the ToolCallBlock component can pick up
    /// the newly-attached SubAgentMessage, subscribe to its StateChanged event,
    /// and render the SubAgentView with live updates.
    /// Also checks if the sub-agent has a pending tool call requiring user action.
    /// </summary>
    private void OnSubAgentStateChanged(SubAgentMessage subAgent)
    {
        // Check if sub-agent has a tool call requiring user action
        if (!string.IsNullOrEmpty(subAgent.PendingToolCallId))
        {
            var toolCallId = subAgent.PendingToolCallId;
            subAgent.PendingToolCallId = null; // Clear to avoid duplicate notifications
            _ = InvokeAsync(async () =>
            {
                try
                {
                    await HandleApprovalRequiredAsync(toolCallId, isSubAgent: true);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error handling sub-agent approval notification");
                }
            });
        }

        InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Called when the main agent's ToolCallHandler has a tool requiring approval or ask_user.
    /// </summary>
    private void OnApprovalRequired(string toolCallId)
    {
        _ = InvokeAsync(async () =>
        {
            try
            {
                await HandleApprovalRequiredAsync(toolCallId, isSubAgent: false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error handling approval notification");
            }
        });
    }

    /// <summary>
    /// Handles a tool call that requires user action (approval or ask_user).
    /// Shows a notification and scrolls to the tool call element with highlight.
    /// </summary>
    private async Task HandleApprovalRequiredAsync(string toolCallId, bool isSubAgent)
    {
        // Show notification
        NotificationService.Notify(new NotificationMessage
        {
            Severity = NotificationSeverity.Warning,
            Summary = isSubAgent
                ? $"{SharedResource.SubAgent}: {SharedResource.ApproveRequired}"
                : SharedResource.ApproveRequired,
            Detail = string.Empty,
            Duration = 10_000,
            ShowProgress = true,
        });

        // Wait for Blazor to render the tool call block, then scroll to it
        await Task.Yield();
        await InvokeAsync(StateHasChanged);
        await Task.Yield();
        await JsRuntime.InvokeVoidAsync("scrollToToolCall", toolCallId);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await JsRuntime.InvokeVoidAsync("setChatHandler", _dotNetRef);
        }
    }

    private async Task OnProfileChangeAsync(object value)
    {
        var profileId = value as string;
        if (!string.IsNullOrEmpty(profileId))
        {
            await ProfileManager.ActivateProfileAsync(profileId);
            NotificationService.Notify(new NotificationMessage
            {
                Severity = NotificationSeverity.Info,
                Summary = "Profile Changed",
                Detail = "Active profile updated.",
                Duration = 1000
            });
        }
    }

    private static void OnEditMessage(VisualChatMessage message)
    {
        message.TempContent = message.Content;
        message.IsEditing = true;
    }

    private static void OnCancelEdit(VisualChatMessage message)
    {
        message.IsEditing = false;
        message.TempContent = string.Empty;
    }

    private async Task OnSaveEditAsync(VisualChatMessage message)
    {
        message.Content = message.TempContent;
        message.Segments.Clear();
        MessageParser.UpdateSegments(message.Content, message);
        message.IsEditing = false;

        ParsePlan(message);

        ChatService.Session.UpdateMessage(message.Id, message.Content);
        await ChatService.SaveSessionAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnDeleteMessageAsync(VisualChatMessage message)
    {
        ChatService.Session.RemoveMessage(message.Id);
        await ChatService.SaveSessionAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnRegenerateLastAsync()
    {
        var lastAssistantMessage = Messages.LastOrDefault(m => m.Role == ChatMessageRole.Assistant);
        if (lastAssistantMessage != null)
        {
            ChatService.Session.RemoveMessage(lastAssistantMessage.Id);
            await GetAiResponseAsync();
        }
    }

    private async Task OnShowSettingsAsync()
    {
        _callSettings = true; // для отрисовки анимации кнопки IsBusy
        await InvokeAsync(StateHasChanged);
        await Task.Yield();

        await DialogService.OpenSideAsync<SettingsDialog>(SharedResource.Settings,
            options: new SideDialogOptions
            {
                CloseDialogOnOverlayClick = true,
                Resizable = true,
                Position = DialogPosition.Right,
                MinHeight = 250.0,
                MinWidth = 400.0
            });

        _callSettings = false;
    }

    public override void Dispose()
    {
        base.Dispose();

        _dotNetRef?.Dispose();
        ChatService.SessionChanged -= HandleSessionChanged;
        SubAgentExecutor.SubAgentStateChanged -= OnSubAgentStateChanged;
        ToolCallHandler.ApprovalRequired -= OnApprovalRequired;

        _cts?.Cancel();
        _cts?.Dispose();

        GC.SuppressFinalize(this);
    }
}

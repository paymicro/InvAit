using System.ComponentModel;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Radzen;
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

        try
        {
            await ChatService.ProcessStreamAsync(
                 assistantMessage,
                 ChatService.CompressSessionAsync(cancellationToken),
                 onContentUpdate: content => MessageParser.UpdateSegments(content, assistantMessage),
                 onStateChange: () =>
                 {
                     assistantMessage.Model ??= ChatService.LastCompletionsModel;
                     InvokeAsync(StateHasChanged);
                 },
                 cancellationToken);
            result = true;
        }
        catch (OperationCanceledException)
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

        try
        {
            await ChatService.ProcessStreamAsync(
                assistantMessage,
                ChatService.GetCompletionsAsync(cancellationToken),
                onContentUpdate: content => MessageParser.UpdateSegments(content, assistantMessage),
                onStateChange: () =>
                {
                    assistantMessage.Model ??= ChatService.LastCompletionsModel;
                    InvokeAsync(StateHasChanged);
                },
                cancellationToken);
            await HandleStreamCompletionAsync(assistantMessage, cancellationToken);
        }
        catch (OperationCanceledException)
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

    private async Task HandleStreamCompletionAsync(VisualChatMessage message, CancellationToken cancellationToken)
    {
        NotifyIfNeeded();
        ParsePlan(message);
        await ChatService.SaveSessionAsync();

        var toolSegments = message.Segments
            .Where(s => s.Type == SegmentType.Tool && s.IsClosed)
            .ToList();

        if (toolSegments.Count > 0)
        {
            await ToolCallHandler.ProcessToolCallsAsync(message, toolSegments, cancellationToken);
            await ChatService.SaveSessionAsync();
            await InvokeAsync(StateHasChanged);

            if (!cancellationToken.IsCancellationRequested)
            {
                await GetAiResponseAsync();
            }
        }
    }

    private void NotifyIfNeeded()
    {
        if (ChatService.FinishReason?.Equals("length", StringComparison.OrdinalIgnoreCase) == true)
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

        if (!string.IsNullOrEmpty(ChatService.LastError))
        {
            NotificationService.Notify(new NotificationMessage
            {
                Severity = NotificationSeverity.Error,
                Summary = ChatService.LastError,
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

            foreach (var toolMsg in chatMessage.ToolResults)
            {
                toolMsg.DisplayName = ToolResult.GetDisplayName(
                    toolMsg.Success,
                    ToolManager.GetTool(toolMsg.Name)?.DisplayName ?? toolMsg.Name);
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

        _cts?.Cancel();
        _cts?.Dispose();

        GC.SuppressFinalize(this);
    }
}

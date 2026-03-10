using System.Collections.Concurrent;
using System.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Radzen;
using Radzen.Blazor.Rendering;
using UIBlazor.Services;
using UIBlazor.Services.Settings;

namespace UIBlazor.Components;

public partial class AiChat : RadzenComponent
{
    private List<VisualChatMessage> Messages => ChatService.Session.Messages;

    private List<SessionSummary> _recentSessions = [];

    private bool IsLoading { get; set; }

    private DotNetObjectReference<AiChat>? _dotNetRef;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<ToolApprovalStatus>> _approvalWaiters = [];

    private CancellationTokenSource _cts = new();

    [Inject] private NotificationService NotificationService { get; set; } = null!;

    [Inject] private IToolManager ToolManager { get; set; } = null!;

    [Inject] private IProfileManager ProfileManager { get; set; } = null!;

    [Inject] private ICommonSettingsProvider CommonSettingsProvider { get; set; } = null!;

    [Inject] private IVsBridge VsBridge { get; set; } = null!;

    [Inject] private IJSRuntime JsRuntime { get; set; } = null!;

    [Inject] private IMessageParser MessageParser { get; set; } = null!;

    [Inject] private ILogger<AiChat> Logger { get; set; } = null!;

    /// <summary>
    /// Starts a new session.
    /// </summary>
    public async Task NewSessionAsync()
    {
        await ChatService.NewSessionAsync();
        _recentSessions = await ChatService.GetRecentSessionsAsync(3);
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Opens the sessions dialog to view or load history.
    /// </summary>
    public async Task OpenSessionsDialogAsync()
    {
        await DialogService.OpenAsync<RecentSessionsPicker>(SharedResource.SessionsTitle,
            options: new DialogOptions {
                Width = "500px",
                Height = "400px",
                AutoFocusFirstElement = true,
                CloseDialogOnOverlayClick = true
            });

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Sends a message programmatically.
    /// </summary>
    /// <param name="content">The message content to send.</param>
    public async Task SendMessageAsync(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || IsLoading)
            return;

        // Process the CurrentInput (HTML) to extract chip data and fetch content
        var processedContent = await ProcessMessageContentAsync(content);

        // Add user message
        var userMessage = new VisualChatMessage
        {
            Content = processedContent,
            Role = ChatMessageRole.User,
            IsExpanded = IsShortMessage(processedContent)
        };

        ChatService.Session.AddMessage(userMessage);
        await ChatService.SaveSessionAsync();
        
        // Get AI response
        await GetAiResponseAsync();
    }

    private async Task<string> ProcessMessageContentAsync(string htmlContent)
    {
        var processedContentBuilder = new StringBuilder();
        var lastIndex = 0;

        // Regex to find chip spans: <span contenteditable="false" class="chip" data-path="path_to_file">display_text</span>
        var chipRegex = new Regex("<span\\s+contenteditable=\"false\"\\s+class=\"chip\"\\s+data-path=\"([^\"]+)\"[^>]*>.*?</span>", RegexOptions.Singleline);

        foreach (Match match in chipRegex.Matches(htmlContent))
        {
            // Append text before the current chip
            processedContentBuilder.Append(htmlContent.Substring(lastIndex, match.Index - lastIndex));

            var dataPath = match.Groups[1].Value;

            // Call the tool to get the content
            var tool = ToolManager.GetTool(BuiltInToolEnum.ReadOpenFile.ToString());
            if (tool != null)
            {
                var args = new Dictionary<string, object> { { "path", dataPath } };
                var vsToolResult = await tool.ExecuteAsync(args);

                if (vsToolResult.Success)
                {
                    processedContentBuilder.Append($"<file_content path=\"{dataPath}\">\n{vsToolResult.Result}\n</file_content>");
                }
                else
                {
                    processedContentBuilder.Append($"<error_reading_file path=\"{dataPath}\">{vsToolResult.ErrorMessage}</error_reading_file>");
                }
            }
            else
            {
                processedContentBuilder.Append($"<error_tool_not_found path=\"{dataPath}\">Tool 'ReadOpenFile' not found.</error_tool_not_found>");
            }

            lastIndex = match.Index + match.Length;
        }

        // Append any remaining text after the last chip
        processedContentBuilder.Append(htmlContent.Substring(lastIndex));

        return processedContentBuilder.ToString();
    }

    private async Task GetAiResponseAsync()
    {
        await _cts.CancelAsync();
        _cts = new CancellationTokenSource();
        await GetAiResponseInternalAsync(0);
    }

    private async Task GetAiResponseInternalAsync(int retryCount)
    {
        IsLoading = true;

        // Add assistant message placeholder
        var assistantMessage = new VisualChatMessage
        {
            Role = ChatMessageRole.Assistant,
            IsStreaming = true,
            IsExpanded = true
        };

        ChatService.Session.AddMessage(assistantMessage);
        await ChatService.SaveSessionAsync();
        await InvokeAsync(StateHasChanged);

        try
        {
            var reasoning = new StringBuilder();
            var response = new StringBuilder();

            await foreach (var delta in ChatService.GetCompletionsAsync(_cts.Token))
            {
                if (delta.ReasoningContent != null)
                {
                    reasoning.Append(delta.ReasoningContent);
                    assistantMessage.ReasoningContent = reasoning.ToString();
                }
                if (delta.Content != null)
                {
                    response.Append(delta.Content);
                    // обновляем сегменты в сообщении
                    MessageParser.UpdateSegments(delta.Content, assistantMessage);
                }

                assistantMessage.Model ??= ChatService.LastCompletionsModel;

                await InvokeAsync(StateHasChanged);
            }

            assistantMessage.Content = response.ToString();
            assistantMessage.IsStreaming = false;

            ParsePlan(assistantMessage);

            await ChatService.SaveSessionAsync();

            // TODO: надо подумать что делать, если прервался на незакрытом тулзе...
            // Думаю нужно выдавать ошибку модели
            await HandleToolCallAsync(assistantMessage, [.. assistantMessage.Segments.Where(s => s.Type == SegmentType.Tool && s.IsClosed)]);
        }
        catch (OperationCanceledException)
        {
            assistantMessage.IsStreaming = false;
        }
        catch (Exception ex)
        {
            var maxRetries = CommonSettingsProvider.Current.MaxRetries;
            assistantMessage.Content = $"Error: {ex.Message} [{retryCount}/{maxRetries}]";
            // обновляем сегменты в сообщении
            MessageParser.UpdateSegments(assistantMessage.Content, assistantMessage);
            assistantMessage.IsStreaming = false;
            Logger.LogError(ex, "Getting response error");

            if (retryCount < maxRetries)
            {
                retryCount++;
                var delay = GetRetryDelay(retryCount);

                assistantMessage.MaxRetryAttempts = maxRetries;
                assistantMessage.RetryAttempt = retryCount;

                try
                {
                    for (var i = delay; i > 0; i--)
                    {
                        assistantMessage.RetryCountdown = i;
                        await InvokeAsync(StateHasChanged);
                        await Task.Delay(1000, _cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    assistantMessage.RetryCountdown = 0;
                    return;
                }

                assistantMessage.RetryCountdown = 0;
                ChatService.Session.RemoveMessage(assistantMessage.Id);
                IsLoading = false;
                await GetAiResponseInternalAsync(retryCount);
                return;
            }
        }
        finally
        {
            IsLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private static int GetRetryDelay(int attempt)
    {
        return attempt switch
        {
            1 => 2,
            2 => 5,
            3 => 10,
            _ => 20
        };
    }

    private static void ParsePlan(VisualChatMessage message)
    {
        if (string.IsNullOrEmpty(message.Content)) return;

        var planRegex = new Regex(@"<plan>(?<plan>.*?)</plan>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var match = planRegex.Match(message.Content);
        if (match.Success)
        {
            message.PlanContent = match.Groups["plan"].Value.Trim();
            // Remove the plan block from display content to avoid double showing
            message.DisplayContent = planRegex.Replace(message.DisplayContent ?? message.Content, string.Empty).Trim();
            if (string.IsNullOrEmpty(message.DisplayContent))
            {
                message.DisplayContent = "Proposed Plan:";
            }
        }
    }

    private async Task ExecutePlanAsync(VisualChatMessage message)
    {
        if (!message.HasPlan) return;

        // Switch mode to Agent
        ChatService.Session.Mode = AppMode.Agent;

        // Send confirmation message to start implementation
        await SendMessageAsync("Implement the plan.");
    }

    private async Task HandleToolApprovalAsync((string MessageId, string SegmentId, bool Approved) args)
    {
        // оно всегда должно быть, потому что мы блокируем UI,
        // пока не придет ответ от модели, и юзер не может кликнуть раньше времени. Но на всякий случай проверим.
        var message = Messages.FirstOrDefault(m => m.Id == args.MessageId);
        if (message != null)
        {
            var status = args.Approved ? ToolApprovalStatus.Approved : ToolApprovalStatus.Rejected;
            message.Segments.FirstOrDefault(s => s.Id == args.SegmentId)?.ApprovalStatus = status;
            await InvokeAsync(StateHasChanged);

            if (_approvalWaiters.TryRemove($"{args.MessageId}_{args.SegmentId}", out var tcs))
            {
                tcs.SetResult(status);
            }
        }
    }

    private async Task HandleToolCallAsync(VisualChatMessage assistantMessage, List<ContentSegment> toolsSegments)
    {
        if (toolsSegments.Count == 0)
        {
            return;
        }

        _approvalWaiters.Clear();

        foreach (var segment in toolsSegments)
        {
            if (_cts.Token.IsCancellationRequested)
            {
                return;
            }

            var tool = ToolManager.GetTool(segment.ToolName);

            VsToolResult vsToolResult;
            if (tool == null)
            {
                vsToolResult = new VsToolResult
                {
                    Name = segment.ToolName,
                    Success = false,
                    ErrorMessage = "Tool not found."
                };
            }
            else
            {
                // Спрашиваем разрешение если нужно
                if (segment.ApprovalStatus == ToolApprovalStatus.Pending)
                {
                    var tcs = new TaskCompletionSource<ToolApprovalStatus>();
                    var waiterKey = $"{assistantMessage.Id}_{segment.Id}";
                    _approvalWaiters[waiterKey] = tcs;

                    try
                    {
                        // Ждем аппрува от пользователя или отмены всего стрима
                        segment.ApprovalStatus = await tcs.Task.WaitAsync(_cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        _approvalWaiters.Clear();
                        return;
                    }
                    finally
                    {
                        _approvalWaiters.TryRemove(waiterKey, out _);
                    }
                }

                if (_cts.Token.IsCancellationRequested)
                {
                    return;
                }

                // Уже должен быть известен статус тулза - или разрешен, или запрещен.
                vsToolResult = segment.ApprovalStatus switch
                {
                    ToolApprovalStatus.Approved => await tool.ExecuteAsync(MessageParser.Parse(segment.ToolName, segment.Lines)),
                    _ => new VsToolResult
                    {
                        Name = segment.ToolName,
                        Success = false,
                        ErrorMessage = "Execution was denied by user."
                    }
                };
            }
#if DEBUG
            // Безголовые (без Visual Studio) тесты
            vsToolResult = HeadlessMocker.GetVsToolResult(vsToolResult);
            Logger.LogTrace("{request} >>>>>> {result}", JsonUtils.Serialize(tool), JsonUtils.Serialize(vsToolResult));
#endif
            // вложенные тулзы — часть сообщения ассистента
            assistantMessage.ToolResults.Add(ToolResult.Convert(vsToolResult, tool.DisplayName, tool.Name));

            await InvokeAsync(StateHasChanged);
        }

        await ChatService.SaveSessionAsync();

        if (_cts.Token.IsCancellationRequested)
        {
            return;
        }

        await GetAiResponseAsync();
    }

    private void LoadMessagesFromSession()
    {
        if (ChatService.Session == null)
        {
            return;
        }

        foreach (var chatMessage in Messages)
        {
            if (chatMessage.Role == ChatMessageRole.Assistant)
            {
                ParsePlan(chatMessage);
            }

            chatMessage.IsExpanded = IsShortMessage(chatMessage.DisplayContent ?? chatMessage.Content);
            MessageParser.UpdateSegments(chatMessage.Content, chatMessage, isHistory: true);

            // восстанавливаем ToolDisplayName для вложенных тулзов
            foreach (var toolMsg in chatMessage.ToolResults)
            {
                toolMsg.DisplayName = ToolResult.GetDisplayName(toolMsg.Success, ToolManager.GetTool(toolMsg.Name)?.DisplayName ?? toolMsg.Name);
            }
        }

        InvokeAsync(StateHasChanged);
    }

    private static bool IsShortMessage(string content)
        => string.IsNullOrEmpty(content) || (content.Length < 1000 && content.Count(c => c == '\n') < 15);

    private async Task CancelResponceAsync() => await _cts.CancelAsync();

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        _dotNetRef = DotNetObjectReference.Create(this);

        ChatService.OnSessionChanged += HandleSessionChanged;

        ToolManager.RegisterAllTools();

        _recentSessions = await ChatService.GetRecentSessionsAsync(3);

        await VsBridge.InitializeAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async void HandleSessionChanged()
    {
        LoadMessagesFromSession();
        _recentSessions = await ChatService.GetRecentSessionsAsync(3);
        await InvokeAsync(StateHasChanged);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await JsRuntime.InvokeVoidAsync("setChatHandler", _dotNetRef);
            await JsRuntime.InvokeVoidAsync("initChatAutoScroll", $"#chat-messages", 70);
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
                Detail = $"Active profile updated.",
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
        message.IsEditing = false;

        // Update display content if it's an assistant message with tools
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
        await DialogService.OpenSideAsync<SettingsDialog>(@SharedResource.Settings,
            options: new SideDialogOptions
            {
                CloseDialogOnOverlayClick = true,
                Resizable = true,
                Position = DialogPosition.Right,
                MinHeight = 250.0,
                MinWidth = 400.0
            });
    }

    /// <inheritdoc />
    protected override string GetComponentCssClass() => ClassList.Create("rz-chat").ToString();

    /// <inheritdoc />
    public override void Dispose()
    {
        base.Dispose();

        _dotNetRef?.Dispose();
        ChatService.OnSessionChanged -= HandleSessionChanged;

        _cts?.Cancel();
        _cts?.Dispose();

        GC.SuppressFinalize(this);
    }
}
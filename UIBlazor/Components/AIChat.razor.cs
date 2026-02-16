using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Radzen;
using Radzen.Blazor.Rendering;
using UIBlazor.Components.Chat;
using UIBlazor.Services;
using UIBlazor.Services.Settings;

namespace UIBlazor.Components;

public partial class AiChat : RadzenComponent
{
    // TODO использовать из ChatService.Session.Messages
    private List<VisualChatMessage> Messages { get; set; } = [];

    private bool IsLoading { get; set; }

    private DotNetObjectReference<AiChat>? _dotNetRef;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<ToolApprovalStatus>> _approvalWaiters = [];

    private CancellationTokenSource _cts = new();

    [Inject]
    private NotificationService NotificationService { get; set; } = null!;

    [Inject]
    private IToolManager ToolManager { get; set; } = null!;

    [Inject]
    private IProfileManager ProfileManager { get; set; } = null!;

    [Inject]
    private IVsBridge VsBridge { get; set; } = null!;

    [Inject]
    private IJSRuntime JsRuntime { get; set; } = null!;

    /// <summary>
    /// Gets or sets the message displayed when there are no messages.
    /// </summary>
    [Parameter]
    public string EmptyMessage { get; set; } = "No messages yet. Start a conversation!";

    /// <summary>
    /// Adds a message to the chat.
    /// </summary>
    public VisualChatMessage AddVisualMessage(VisualChatMessage chatMessage, bool updateState = true)
    {
        // если не обновляем состояние - то это загрузка истории
        UpdateSegments(chatMessage.Content, chatMessage, isHistory: !updateState); 
        Messages.Add(chatMessage);

        // Limit the number of messages
        if (Messages.Count > ChatService.Options.MaxMessages)
        {
            Messages.RemoveAt(0);
        }

        if (updateState)
        {
            InvokeAsync(StateHasChanged);
        }
        return chatMessage;
    }

    /// <summary>
    /// Clears all messages from the chat.
    /// </summary>
    public async Task ClearChatAsync()
    {
        Messages.Clear();

        // Clear the session in the AI service
        await ChatService.ClearSessionAsync();

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
        AddVisualMessage(userMessage);
        await ChatService.AddMessageAsync(userMessage);

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

    /// <summary>
    /// Текущий активный сегмент.
    /// </summary>
    private ContentSegment? _activeSegment;

    /// <summary>
    /// Наполнение сегментов <see cref="VisualChatMessage"/>
    /// </summary>
    /// <param name="delta">Дельта или весь текст контента</param>
    /// <param name="assistant">Сообщение ассистента</param>
    /// <param name="isHistory">Подгрузка истории сессии</param>
    public void UpdateSegments(string delta, VisualChatMessage assistant, bool isHistory = false)
    {
        if (string.IsNullOrEmpty(delta))
            return;

        if (isHistory)
        {
            // В истории дельта - это целое неизменное сообщение.
            // Поэтому активный сегмент всегда новый для каждого вызова.
            _activeSegment = null;
        }

        var incomingText = delta;

        while (!string.IsNullOrEmpty(incomingText))
        {
            // Логика сегментов
            if (_activeSegment == null || _activeSegment.IsClosed)
            {
                _activeSegment = new ContentSegment();
                assistant.Segments.Add(_activeSegment);
            }

            var openIdx = FindOpeningTag(incomingText, out var openTagName);
            var closeIdx = _activeSegment.Type is not SegmentType.Unknown and not SegmentType.Markdown
                           ? incomingText.IndexOf($"</{_activeSegment.TagName}>")
                           : -1;

            // Сценарий А: Закрытие
            if (closeIdx != -1 && (openIdx == -1 || closeIdx < openIdx))
            {
                var closingTag = $"</{_activeSegment.TagName}>";
                var endOfTag = closeIdx + closingTag.Length;
                _activeSegment.AppendToken(incomingText[..endOfTag]);
                _activeSegment.Close();
                incomingText = incomingText[endOfTag..];
                continue;
            }

            // Сценарий Б: Открытие
            if (openIdx != -1)
            {
                if (openIdx > 0)
                {
                    _activeSegment.AppendToken(incomingText[..openIdx]);
                    _activeSegment.Close();
                    incomingText = incomingText[openIdx..];
                    continue;
                }

                // Находим конец тега '>', чтобы знать, где кончаются параметры (name="...")
                var tagEndIdx = incomingText.IndexOf(">");
                if (tagEndIdx != -1)
                {
                    var consumptionLength = tagEndIdx + 1;
                    _activeSegment.AppendToken(incomingText.Substring(0, consumptionLength));
                    if (_activeSegment.Type == SegmentType.Tool && !string.IsNullOrEmpty(_activeSegment.ToolName))
                    {
                        if (isHistory) // При загрузке истории все тулзы заапрувлены. Чтобы не было ложного Pending.
                        {
                            _activeSegment.ApprovalStatus = ToolApprovalStatus.Approved;
                        }
                        else
                        {
                            // ну и сразу ставим статус, чтобы не было гонки между рендером и обновлением статуса после получения всех параметров
                            _activeSegment.ApprovalStatus = ToolManager.GetApprovalModeByToolName(_activeSegment.ToolName) == ToolApprovalMode.Manual
                                ? ToolApprovalStatus.Pending
                                : ToolApprovalStatus.Approved;
                        }
                    }
                    incomingText = incomingText.Substring(consumptionLength);
                    continue;
                }
            }

            // Сценарий В: Обычный контент
            _activeSegment.AppendToken(incomingText);
            incomingText = string.Empty;
        }
    }

    private int FindOpeningTag(string text, out string tagName)
    {
        tagName = "";
        int planIdx = text.IndexOf("<plan");
        int funcIdx = text.IndexOf("<function");

        if (planIdx == -1 && funcIdx == -1) return -1;

        // Берем тот, что встретился раньше
        if (planIdx != -1 && (funcIdx == -1 || planIdx < funcIdx))
        {
            tagName = "plan";
            return planIdx;
        }
        tagName = "function";
        return funcIdx;
    }

    private async Task GetAiResponseAsync()
    {
        IsLoading = true;

        await _cts.CancelAsync();

        _cts = new CancellationTokenSource();

        // Add assistant message placeholder
        var assistantMessage = AddVisualMessage(new VisualChatMessage {
            Role = ChatMessageRole.Assistant,
            IsStreaming = true,
            IsExpanded = true
        });

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

                    UpdateSegments(delta.Content, assistantMessage);
                }

                assistantMessage.Model ??= ChatService.LastCompletionsModel;

                await InvokeAsync(StateHasChanged);
            }

            assistantMessage.Content = response.ToString();
            assistantMessage.IsStreaming = false;

            ParsePlan(assistantMessage);

            await ChatService.AddMessageAsync(assistantMessage);

            // TODO: надо подумать что делать, если прервался на незакрытом тулзе...
            await HandleToolCallAsync(assistantMessage, [.. assistantMessage.Segments.Where(s => s.Type == SegmentType.Tool && s.IsClosed)]);
        }
        catch (Exception ex)
        {
            assistantMessage.Content = $"Sorry, I encountered an error: {ex.Message}";
            assistantMessage.IsStreaming = false;
            await InvokeAsync(StateHasChanged);
        }
        finally
        {
            IsLoading = false;
            await InvokeAsync(StateHasChanged);
        }
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
        
        // Notify VS Host if needed (though it's mostly for system prompt generation in UI)
        await VsBridge.ExecuteToolAsync(BuiltInToolEnum.SwitchMode, new Dictionary<string, object> { { "param1", "Agent" } });

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

            if (tool != null)
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
                        _approvalWaiters.TryRemove(waiterKey, out _);
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
                var vsToolResult = segment.ApprovalStatus switch
                {
                    ToolApprovalStatus.Approved => await tool.ExecuteAsync(ToolManager.Parse(segment.ToolName, segment.Lines)),
                    _ => new VsToolResult { Name = segment.ToolName, Success = false, ErrorMessage = "Execution was denied by user." }
                };
#if DEBUG
                // Безголовые (без Visual Studio) тесты
                if (!vsToolResult.Success && vsToolResult.ErrorMessage == "WebView2 API is`t find.")
                {
                    switch (vsToolResult.Name)
                    {
                        case BuiltInToolEnum.ReadOpenFile:
                            vsToolResult = new VsToolResult
                            {
                                Result = """
                                namespace UIBlazor.Components;

                                public partial class AIChat : TestComponent
                                {
                                    private List<ChatMessage> Messages { get; set; } = [];
                                }
                                """
                            };
                            break;
                        case BuiltInToolEnum.ReadFiles:
                            vsToolResult = new VsToolResult
                            {
                                Result = """
                                File content
                                1 |namespace UIBlazor.Components;
                                2 |
                                3 |public partial class AIChat : TestComponent
                                4 |{
                                5 |    private List<ChatMessage> Messages { get; set; } = [];
                                6 |}
                                """
                            };
                            break;
                        case BuiltInToolEnum.ApplyDiff:
                            vsToolResult = new VsToolResult
                            {
                                Result = "All replacements is successful."
                            };
                            break;
                    }
                }
#endif

                // для модели обогащаем результат и отправляем в чат
                var result = $"""
                             <tool_result name="{tool.Name}" success={vsToolResult.Success}>
                             {(vsToolResult.Success ? vsToolResult.Result : vsToolResult.ErrorMessage)}
                             </tool_result>
                             """;

                var toolSessionMessage = new VisualChatMessage
                {
                    Role = vsToolResult.Role,
                    Content = result,
                    DisplayContent = (vsToolResult.Success ? "✅ " : "❌ ") + tool.DisplayName ?? tool.Name
                };

                // идет в сессию
                await ChatService.AddMessageAsync(toolSessionMessage);

                var toolResultMessage = new VisualChatMessage
                {
                    Id = toolSessionMessage.Id, // синхронизируем Id. Для показа в UI и удаления
                    Role = ChatMessageRole.Tool,
                    Content = vsToolResult.Result,
                    ToolDisplayName = tool.Name,
                };

                assistantMessage.ToolMessages.Add(toolResultMessage);

                await InvokeAsync(StateHasChanged);
            }
        }

        if (_cts.Token.IsCancellationRequested)
        {
            return;
        }

        await GetAiResponseAsync();
    }

    private void SyncSessionMessageWithUi()
    {
        if (ChatService.Session == null) return;

        Messages.Clear();
        VisualChatMessage? lastAssistantMessage = null;
        foreach (var chatMessage in ChatService.Session.Messages)
        {
            // тулзы показываем по особому (смотри HandleToolCallAsync)
            var regex = Regex.Match(chatMessage.Content, "^<tool_result name=\"(?<name>.{2,40})\" success=(?<success>[T|t]rue|[F|f]alse)>(?<result>.*)</tool_result>", RegexOptions.Singleline);
            if (regex.Success)
            {
                var isSuccess = string.Equals(regex.Groups["success"].Value, "True", StringComparison.OrdinalIgnoreCase);
                var toolDisplayName = (isSuccess ? "✅ " : "❌ ") + ToolManager.GetTool(regex.Groups["name"].Value)?.DisplayName ?? regex.Groups["name"].Value;

                var toolResultMessage = new VisualChatMessage
                {
                    Id = chatMessage.Id,
                    Role = ChatMessageRole.Tool,
                    Content = regex.Groups["result"].Value,
                    ToolDisplayName = toolDisplayName,
                };

                if (lastAssistantMessage != null)
                {
                    lastAssistantMessage.ToolMessages.Add(toolResultMessage);
                }
                else
                {
                    // Fallback if somehow there's a tool result without an assistant message before it
                    AddVisualMessage(toolResultMessage, updateState: false);
                }
            }
            else
            {
                if (chatMessage.Role == ChatMessageRole.Assistant)
                {
                    ParsePlan(chatMessage);
                    lastAssistantMessage = chatMessage;
                }
                else if (chatMessage.Role == ChatMessageRole.User)
                {
                    lastAssistantMessage = null;
                }
                
                chatMessage.IsExpanded = IsShortMessage(chatMessage.DisplayContent ?? chatMessage.Content);
                AddVisualMessage(chatMessage, updateState: false);
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
        await JsRuntime.InvokeVoidAsync("setChatHandler", _dotNetRef);

        ChatService.OnSessionChanged += HandleSessionChanged;

        ToolManager.RegisterAllTools();
        await ProfileManager.InitializeAsync();

        await VsBridge.InitializeAsync();
        VsBridge.OnModeSwitched += HandleModeSwitched;

        await InvokeAsync(StateHasChanged);
    }

    private void HandleSessionChanged()
    {
        SyncSessionMessageWithUi();
        InvokeAsync(StateHasChanged);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await JsRuntime.InvokeVoidAsync("initChatAutoScroll", $"#chat-messages", 70);
        }
    }

    private void HandleModeSwitched(AppMode mode)
    {
        ChatService.Session.Mode = mode;
        InvokeAsync(StateHasChanged);
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
        Messages.Remove(message);
        ChatService.Session.RemoveMessage(message.Id);
        
        foreach (var toolMsg in message.ToolMessages)
        {
            ChatService.Session.RemoveMessage(toolMsg.Id);
        }

        await ChatService.SaveSessionAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnRegenerateLastAsync()
    {
        var lastAssistantMessage = Messages.LastOrDefault(m => m.Role == ChatMessageRole.Assistant);
        if (lastAssistantMessage != null)
        {
            Messages.Remove(lastAssistantMessage);
            ChatService.Session.RemoveMessage(lastAssistantMessage.Id);

            foreach (var toolMsg in lastAssistantMessage.ToolMessages)
            {
                ChatService.Session.RemoveMessage(toolMsg.Id);
            }

            await GetAiResponseAsync();
        }
    }

    private async Task OnShowSettingsAsync()
    {
        await DialogService.OpenSideAsync<SettingsDialog>("Settings", options: new SideDialogOptions {
            CloseDialogOnOverlayClick = true,
            Resizable = false,
            Position = DialogPosition.Right,
            MinHeight = 250.0,
            MinWidth = 450.0
        });
        
        // Reload profiles in case they were changed
        await InvokeAsync(StateHasChanged);
    }

    /// <inheritdoc />
    protected override string GetComponentCssClass() => ClassList.Create("rz-chat").ToString();

    /// <inheritdoc />
    public override void Dispose()
    {
        base.Dispose();
        
        _dotNetRef?.Dispose();
        VsBridge.OnModeSwitched -= HandleModeSwitched;
        ChatService.OnSessionChanged -= HandleSessionChanged;

        _cts?.Cancel();
        _cts?.Dispose();

        GC.SuppressFinalize(this);
    }
}
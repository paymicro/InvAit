using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor.Rendering;
using UIBlazor.Services;
using UIBlazor.Services.Settings;

namespace UIBlazor.Components;

public partial class AiChat : RadzenComponent
{
    // TODO использовать из ChatService.Session.Messages
    private List<VisualChatMessage> Messages { get; set; } = [];
    // private string CurrentInput { get; set; } = string.Empty;
    private bool IsLoading { get; set; }

    private bool _preventDefault;

    private CancellationTokenSource _cts = new();

    [Inject]
    private NotificationService NotificationService { get; set; } = null!;

    [Inject]
    private IToolManager ToolManager { get; set; } = null!;

    [Inject]
    private IProfileManager ProfileManager { get; set; } = null!;

    [Inject]
    private IVsBridge VsBridge { get; set; } = null!;

    /// <summary>
    /// Gets or sets the message displayed when there are no messages.
    /// </summary>
    [Parameter]
    public string EmptyMessage { get; set; } = "No messages yet. Start a conversation!";

    /// <summary>
    /// Gets or sets the text displayed in the user avatar.
    /// </summary>
    [Parameter]
    public string UserAvatarText { get; set; } = "U";

    /// <summary>
    /// Adds a message to the chat.
    /// </summary>
    public VisualChatMessage AddVisualMessage(VisualChatMessage chatMessage)
    {
        Messages.Add(chatMessage);

        // Limit the number of messages
        if (Messages.Count > ChatService.Options.MaxMessages)
        {
            Messages.RemoveAt(0);
        }

        InvokeAsync(StateHasChanged);
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
        var processedContent = await ProcessMessageContent(content);

        // Add user message
        var userMessage = new VisualChatMessage
        {
            Content = processedContent,
            Role = ChatMessageRole.User
        };
        AddVisualMessage(userMessage);
        await ChatService.AddMessageAsync(userMessage);

        // Get AI response
        await GetAiResponseAsync();
    }

    private async Task<string> ProcessMessageContent(string htmlContent)
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
        IsLoading = true;

        await _cts.CancelAsync();

        _cts = new CancellationTokenSource();

        // Add assistant message placeholder
        var assistantMessage = AddVisualMessage(new VisualChatMessage {
            Role = ChatMessageRole.Assistant,
            IsStreaming = true
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
                    assistantMessage.Content = response.ToString();
                }
                assistantMessage.Model ??= ChatService.LastCompletionsModel;

                await InvokeAsync(StateHasChanged);
            }

            assistantMessage.IsStreaming = false;

            var tools = ToolManager.ParseToolBlock(assistantMessage.Content);

            // Меняем контент если там есть вызов тулзов
            if (tools.Count > 0)
            {
                assistantMessage.Content = "Calling tool: " + string.Join(", ", tools.Select(t => t.Function.Name));
            }

            // Add assistant response to conversation history
            await ChatService.AddMessageAsync(assistantMessage);

            await HandleToolCallAsync(tools);
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

    private async Task HandleToolCallAsync(List<AiTool> aiTools)
    {
        if (aiTools.Count == 0)
        {
            return;
        }

        foreach (var aiTool in aiTools)
        {
            var tool = ToolManager.GetTool(aiTool.Function.Name);
            if (tool != null)
            {
                var approvalMode = ToolManager.Current.CategoryStates.TryGetValue(tool.Category, out var state)
                    ? state.ApprovalMode
                    : ToolApprovalMode.AutoApprove;

                if (approvalMode == ToolApprovalMode.Manual)
                {
                    var confirmed = await DialogService.Confirm(
                        $"The AI wants to use the tool '{tool.Name}' with the following parameters:\n\n" +
                        string.Join("\n", aiTool.Function.Arguments.Select(a => $"{a.Key}: {a.Value}")),
                        "Approval Required",
                        new ConfirmOptions { OkButtonText = "Allow", CancelButtonText = "Deny" });

                    if (confirmed != true)
                    {
                        var deniedMsg = new VisualChatMessage { Role = ChatMessageRole.System, Content = $"Execution of '{tool.Name}' was denied by user." };
                        AddVisualMessage(deniedMsg);
                        await ChatService.AddMessageAsync(deniedMsg);
                        continue;
                    }
                }

                var vsToolResult = await tool.ExecuteAsync(aiTool.Function.Arguments);
                NotificationService.Notify(new NotificationMessage
                {
                    Severity = NotificationSeverity.Success,
                    Summary = vsToolResult.Result,
                    Duration = 3_000,
                    ShowProgress = true
                });
#if DEBUG
                // Безголовые тесты
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

                var toolResultMessage = new VisualChatMessage
                {
                    Role = ChatMessageRole.Tool,
                    Content = vsToolResult.Result,
                    ToolName = tool.Name,
                };
                AddVisualMessage(toolResultMessage);

                // для модели обогащаем результат и отправляем в чат
                var result = vsToolResult.Success
                    ? $"""
                       <tool_result name="{tool.Name}">
                       {vsToolResult.Result}
                       </tool_result>
                       Инструкция: На основе полученных данных выше, реши, достигнута ли цель. Если нет - предложи следующее действие, НО не повторяй предыдущее.
                       """
                    : $"""
                       <tool_result name="{tool.Name}">
                       {vsToolResult.ErrorMessage}
                       </tool_result>
                       Инструкция: Во время выполнения возникла ошибка. Предложи следующее действие, НО не повторяй предыдущее.
                       """;
                await ChatService.AddMessageAsync(new VisualChatMessage
                {
                    Role = vsToolResult.Role,
                    Content = result
                });
            }
        }

        await GetAiResponseAsync();
    }

    private void SyncSessionMessageWithUi()
    {
        foreach (var chatMessage in ChatService.Session.Messages)
        {
            // тулзы показываем по особому (смотри HandleToolCallAsync)
            var regex = Regex.Match(chatMessage.Content, "^<tool_result name=\"(?<name>.{2,20})\">(?<result>.*)</tool_result>", RegexOptions.Singleline);
            if (regex.Success)
            {
                AddVisualMessage(new VisualChatMessage
                {
                    Role = ChatMessageRole.Tool,
                    Content = regex.Groups["result"].Value,
                    ToolName = regex.Groups["name"].Value,
                });
            }
            else
            {
                AddVisualMessage(chatMessage);
            }
        }
    }

    private async Task CancelResponceAsync()
    {
        await _cts.CancelAsync();
    }

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();

        ToolManager.RegisterAllTools();
        await ProfileManager.InitializeAsync();

        await VsBridge.InitializeAsync();
        VsBridge.OnModeSwitched += HandleModeSwitched;

        SyncSessionMessageWithUi();

        await InvokeAsync(StateHasChanged);
    }

    private void HandleModeSwitched(AppMode mode)
    {
        ChatService.Session.Mode = mode;
        InvokeAsync(StateHasChanged);
    }

    private async Task OnProfileChange(object value)
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
                Duration = 2000
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
        ChatService.Session.UpdateMessage(message.Id, message.Content);
        await ChatService.SaveSessionAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnDeleteMessageAsync(VisualChatMessage message)
    {
        Messages.Remove(message);
        ChatService.Session.RemoveMessage(message.Id);
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
            await GetAiResponseAsync();
        }
    }

    private async Task OnShowSettingsAsync()
    {
        await DialogService.OpenSideAsync<AiSettings>("Settings", options: new SideDialogOptions {
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
        
        VsBridge.OnModeSwitched -= HandleModeSwitched;

        _cts?.Cancel();
        _cts?.Dispose();

        GC.SuppressFinalize(this);
    }
}
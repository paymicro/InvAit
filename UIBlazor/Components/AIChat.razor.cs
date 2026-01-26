using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Radzen;
using Radzen.Blazor.Rendering;
using Shared.Contracts;
using UIBlazor.Agents;
using UIBlazor.Models;
using UIBlazor.Services;
using UIBlazor.Services.Settings;
using UIBlazor.VS;

namespace UIBlazor.Components;

public partial class AIChat : RadzenComponent
{
    // TODO использовать из ChatService.Session.Messages
    private List<VisualChatMessage> Messages { get; set; } = [];
    private string CurrentInput { get; set; } = string.Empty;
    private bool IsLoading { get; set; }

    private bool _preventDefault;
    private ElementReference _inputElement;
    private ElementReference _messagesContainer;
    private CancellationTokenSource _cts = new();

    [Inject]
    private NotificationService NotificationService { get; set; } = null!;

    [Inject]
    private IToolManager ToolManager { get; set; } = null!;

    [Inject]
    private IProfileManager ProfileManager { get; set; } = null!;

    [Inject]
    private IVsBridge VsBridge { get; set; } = null!;

    private List<AppMode> AppModeValues { get; } = Enum.GetValues<AppMode>().ToList();

    /// <summary>
    /// Specifies additional custom attributes that will be rendered by the input.
    /// </summary>
    /// <value>The attributes.</value>
    [Parameter]
    public IReadOnlyDictionary<string, object>? InputAttributes { get; set; }

    /// <summary>
    /// Gets or sets the title displayed in the chat header.
    /// </summary>
    [Parameter]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the placeholder text for the input field.
    /// </summary>
    [Parameter]
    public string Placeholder { get; set; } = "Type your message...";

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
    /// Gets or sets whether to show the clear chat button.
    /// </summary>
    [Parameter]
    public bool ShowClearButton { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the chat is disabled.
    /// </summary>
    [Parameter]
    public bool Disabled { get; set; }

    /// <summary>
    /// Gets or sets whether the input is read-only.
    /// </summary>
    [Parameter]
    public bool ReadOnly { get; set; }

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
        if (string.IsNullOrWhiteSpace(content) || Disabled || IsLoading)
            return;

        // Clear input
        CurrentInput = string.Empty;
        await InvokeAsync(StateHasChanged);

        // Add user message
        var userMessage = new VisualChatMessage
        {
            Content = content,
            Role = ChatMessageRole.User
        };
        AddVisualMessage(userMessage);
        await ChatService.AddMessageAsync(userMessage);

        // Get AI response
        await GetAIResponseAsync();
    }

    private async Task GetAIResponseAsync()
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
                if (tool.ApprovalMode == ToolApprovalMode.Disabled)
                {
                    var disabledMsg = new VisualChatMessage { Role = ChatMessageRole.System, Content = $"Tool '{tool.Name}' is disabled." };
                    await ChatService.AddMessageAsync(disabledMsg);
                    continue;
                }

                if (tool.ApprovalMode == ToolApprovalMode.Manual)
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
                if (vsToolResult != null)
                {
                    NotificationService.Notify(new NotificationMessage
                    {
                        Severity = NotificationSeverity.Success,
                        Summary = vsToolResult.Result,
                        Duration = 3_000,
                        ShowProgress = true
                    });
                }
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
                await ChatService.AddMessageAsync(toolResultMessage);
                
                if (vsToolResult.Success)
                {
                    var result = $"""
                        <tool_result name="{tool.Name}">
                        {vsToolResult.Result}
                        </tool_result>
                        Инструкция: На основе полученных данных выше, реши, достигнута ли цель. Если нет - предложи следующее действие, НО не повторяй предыдущее.
                        """ ;
                    var systemFollowup = new VisualChatMessage { Role = vsToolResult.Role, Content = result };
                    await ChatService.AddMessageAsync(systemFollowup);
                }
                else
                {
                    var result = $"""
                        <tool_result name="{tool.Name}">
                        {vsToolResult.ErrorMessage}
                        </tool_result>
                        Инструкция: Во время выполнения возникла ошибка. Предложи следующее действие, НО не повторяй предыдущее.
                        """;
                    var systemError = new VisualChatMessage { Role = ChatMessageRole.System, Content = result };
                    await ChatService.AddMessageAsync(systemError);
                }
            }
        }

        await GetAIResponseAsync();
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
        ProfileManager.InitializeAsync();

        await ChatService.LoadLastSessionOrGenerateNewAsync();
        foreach (var chatMessage in ChatService.Session.Messages)
        {
            AddVisualMessage(chatMessage);
        }

        VsBridge.OnModeSwitched += HandleModeSwitched;

        await InvokeAsync(StateHasChanged);
    }

    private void HandleModeSwitched(AppMode mode)
    {
        if (ChatService.Session != null)
        {
            ChatService.Session.Mode = mode;
            _ = ChatService.AddMessageAsync(ChatMessageRole.System, $"Mode switched to {mode}");
        }
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

    private async Task OnInputAsync(ChangeEventArgs e)
    {
        CurrentInput = e.Value?.ToString() ?? "";
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnKeyDownAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !e.ShiftKey && JSRuntime != null)
        {
            _preventDefault = true;
            await OnSendMessageAsync();
        }
        else
        {
            _preventDefault = false;
        }
    }

    private async Task OnSendMessageAsync()
    {
        await SendMessageAsync(CurrentInput);
    }

    private async Task OnClearChatAsync()
    {
        await ClearChatAsync();
    }

    private void OnEditMessage(VisualChatMessage message)
    {
        message.TempContent = message.Content;
        message.IsEditing = true;
    }

    private void OnCancelEdit(VisualChatMessage message)
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
            await GetAIResponseAsync();
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
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
            return;

        if (_messagesContainer.Context != null && JSRuntime != null)
        {
            // Scroll to bottom when new messages are added
            await JSRuntime.InvokeVoidAsync("scrollToBottomIfNeeded", ".rz-chat-messages", 100);
        }
    }

    /// <inheritdoc />
    protected override string GetComponentCssClass()
    {
        return ClassList.Create("rz-chat").ToString();
    }

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
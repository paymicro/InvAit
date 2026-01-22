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
using ChatMessage = UIBlazor.Models.ChatMessage;

namespace UIBlazor.Components;

public partial class AIChat : RadzenComponent
{
    private List<ChatMessage> Messages { get; set; } = [];
    private string CurrentInput { get; set; } = string.Empty;
    private bool IsLoading { get; set; }

    private bool _preventDefault;
    private ElementReference _inputElement;
    private ElementReference _messagesContainer;
    private CancellationTokenSource _cts = new();
    private string? _currentSessionId;

    [Inject]
    private NotificationService NotificationService { get; set; } = null!;

    [Inject]
    private ToolManager ToolManager { get; set; } = null!;

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
    /// Gets or sets the text displayed in the assistant avatar.
    /// </summary>
    [Parameter]
    public string ToolAvatarText { get; set; } = "T";

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
    /// Gets or sets the message template.
    /// </summary>
    /// <value>The message template.</value>
    [Parameter]
    public RenderFragment<ChatMessage>? MessageTemplate { get; set; }

    /// <summary>
    /// Gets or sets the empty template shown when there are no messages.
    /// </summary>
    /// <value>The empty template.</value>
    [Parameter]
    public RenderFragment? EmptyTemplate { get; set; }

    /// <summary>
    /// Gets the current list of messages.
    /// </summary>
    public IReadOnlyList<ChatMessage> GetMessages() => Messages.AsReadOnly();

    /// <summary>
    /// Adds a message to the chat.
    /// </summary>
    /// <param name="content">The message content.</param>
    /// <returns>The created message.</returns>
    public ChatMessage AddVisualMessage(string content, string role)
    {
        return AddVisualMessage(new ChatMessage
        {
            Content = content,
            Role = role
        });
    }

    public ChatMessage AddVisualMessage(ChatMessage chatMessage)
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
        if (!string.IsNullOrEmpty(_currentSessionId))
        {
            await ChatService.ClearSessionAsync(_currentSessionId);
        }

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
        var userMessage = AddVisualMessage(content, ChatMessageRole.User);

        await ChatService.AddMessageAsync(_currentSessionId, ChatMessageRole.User, content);

        // Get AI response
        await GetAIResponseAsync();
    }

    /// <summary>
    /// Loads conversation history from the AI service session.
    /// </summary>
    public async Task LoadConversationHistoryAsync()
    {
        if (string.IsNullOrEmpty(_currentSessionId))
            return;

        var session = await ChatService.GetOrCreateSessionAsync(_currentSessionId);

        // Clear current messages
        Messages.Clear();

        // Add messages from session history
        foreach (var message in session.Messages)
        {
            Messages.Add(message);

            // Limit the number of messages
            if (Messages.Count > ChatService.Options.MaxMessages)
            {
                Messages.RemoveAt(0);
            }
        }

        await InvokeAsync(StateHasChanged);
    }

    private string GenerateSessionId() => $"session_{DateTime.Now:s}";

    private async Task GetAIResponseAsync()
    {
        IsLoading = true;

        await _cts.CancelAsync();

        _cts = new CancellationTokenSource();

        // Ensure we have a session ID
        if (string.IsNullOrEmpty(_currentSessionId))
        {
            _currentSessionId = GenerateSessionId();
        }

        // Add assistant message placeholder
        var assistantMessage = AddVisualMessage("", ChatMessageRole.Assistant);
        assistantMessage.IsStreaming = true;

        try
        {
            var reasoning = new StringBuilder();
            var response = new StringBuilder();
            await foreach (var delta in ChatService.GetCompletionsAsync(_currentSessionId, _cts.Token))
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

                await InvokeAsync(StateHasChanged);
            }

            assistantMessage.IsStreaming = false;

            var tools = ToolManager.ParseToolBlock(assistantMessage.Content);

            // Меняем контент если там есть вызов тулзов
            if (tools.Count > 0)
            {
                assistantMessage.Content = "Tool: " + string.Join(", ", tools.Select(t => t.Function.Name));
            }

            // Add assistant response to conversation history
            await ChatService.AddMessageAsync(_currentSessionId, ChatMessageRole.Assistant, assistantMessage.Content);

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

                AddVisualMessage(new ChatMessage
                {
                    Role = ChatMessageRole.Tool,
                    Content = vsToolResult.Result,
                    ToolName = tool.Name,
                });
                if (vsToolResult.Success)
                {
                    var result = $"""
                        <tool_result name="{tool.Name}">
                        {vsToolResult.Result}
                        </tool_result>
                        Инструкция: На основе полученных данных выше, реши, достигнута ли цель. Если нет - предложи следующее действие, НО не повторяй предыдущее.
                        """ ;
                    await ChatService.AddMessageAsync(_currentSessionId, vsToolResult.Role, result);
                }
                else
                {
                    var result = $"""
                        <tool_result name="{tool.Name}">
                        {vsToolResult.ErrorMessage}
                        </tool_result>
                        Инструкция: Во время выполнения возникла ошибка. Предложи следующее действие, НО не повторяй предыдущее.
                        """;
                    await ChatService.AddMessageAsync(_currentSessionId, ChatMessageRole.System, vsToolResult.ErrorMessage);
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
        _currentSessionId = GenerateSessionId();
        ToolManager.RegisterAllTools();
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
            // await JSRuntime.InvokeAsync<string>("Radzen.setInputValue", inputElement, "");
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

    private async Task OnShowSettingsAsync()
    {
        await DialogService.OpenSideAsync<AiSettings>("Settings", options: new SideDialogOptions {
            CloseDialogOnOverlayClick = true,
            Resizable = false,
            Position = DialogPosition.Right,
            MinHeight = 250.0,
            MinWidth = 400.0
        });
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender && _messagesContainer.Context != null && JSRuntime != null)
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

        _cts?.Cancel();
        _cts?.Dispose();

        GC.SuppressFinalize(this);
    }
}
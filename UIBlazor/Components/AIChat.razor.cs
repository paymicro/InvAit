using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Radzen;
using Radzen.Blazor.Rendering;
using Shared.Contracts;
using UIBlazor.Agents;
using UIBlazor.Models;
using UIBlazor.Services;
using UIBlazor.Utils;
using UIBlazor.VS;
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
    private LocalStorageService Storage { get; set; } = null!;

    [Inject]
    private IVsBridge VsBridge { get; set; } = null!;

    [Inject]
    private BuiltInAgent BuiltInAgent { get; set; } = null!;

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
    public string AssistantAvatarText { get; set; } = "AI";

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
    /// Event callback that is invoked when a new message is added.
    /// </summary>
    [Parameter]
    public EventCallback<ChatMessage> MessageAdded { get; set; }

    /// <summary>
    /// Event callback that is invoked when the chat is cleared.
    /// </summary>
    [Parameter]
    public EventCallback ChatCleared { get; set; }

    /// <summary>
    /// Event callback that is invoked when a message is sent.
    /// </summary>
    [Parameter]
    public EventCallback<string> MessageSent { get; set; }

    /// <summary>
    /// Event callback that is invoked when the AI response is received.
    /// </summary>
    [Parameter]
    public EventCallback<string> ResponseReceived { get; set; }

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
        var message = new ChatMessage
        {
            Content = content,
            Role = role,
            Timestamp = DateTime.Now
        };

        Messages.Add(message);

        // Limit the number of messages
        if (Messages.Count > ChatService.Options.MaxMessages)
        {
            Messages.RemoveAt(0);
        }

        InvokeAsync(StateHasChanged);
        return message;
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

        await ChatCleared.InvokeAsync();
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
        await MessageAdded.InvokeAsync(userMessage);
        await MessageSent.InvokeAsync(content);

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
            var response = "";
            await foreach (var token in ChatService.GetCompletionsAsync(_currentSessionId, _cts.Token))
            {
                response += token;
                assistantMessage.Content = response;
                await InvokeAsync(StateHasChanged);
            }

            assistantMessage.IsStreaming = false;
            await ResponseReceived.InvokeAsync(response);
            await MessageAdded.InvokeAsync(assistantMessage);

            var tools = ChatService.ParseToolBlock(response);
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
                var vsToolResult = await tool.ExecuteAsync(JsonUtils.DeserializeParameters(aiTool.Function.Arguments));
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

                AddVisualMessage(vsToolResult.Result, vsToolResult.Role);
                await ChatService.AddMessageAsync(_currentSessionId, vsToolResult.Role, vsToolResult.Result);
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

    private async Task OnTestAsync()
    {
        var result = await VsBridge.ExecuteToolAsync(BuiltInToolEnum.GetErrors);
        NotificationService.Notify(new NotificationMessage
        {
            Severity = NotificationSeverity.Info,
            Summary = result.Result,
            Duration = 15_000,
            ShowProgress = true
        });
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender && _messagesContainer.Context != null && JSRuntime != null)
        {
            // Scroll to bottom when new messages are added
            await JSRuntime.InvokeVoidAsync("eval",
                "setTimeout(() => { " +
                "const container = document.querySelector('.rz-chat-messages'); " +
                "if (container) container.scrollTop = container.scrollHeight; " +
                "}, 100);");
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
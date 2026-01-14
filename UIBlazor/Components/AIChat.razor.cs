using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Radzen;
using Radzen.Blazor;
using Radzen.Blazor.Rendering;
using Shared.Contracts;
using UIBlazor.Agents;
using UIBlazor.Services;
using UIBlazor.Utils;
using UIBlazor.VS;

namespace UIBlazor.Components;

public partial class AIChat(AiSettingsProvider aiSettingsProvider) : RadzenComponent
{
    private List<ChatMessage> Messages { get; set; } = [];
    private string CurrentInput { get; set; } = string.Empty;
    private bool IsLoading { get; set; }
    private bool preventDefault;
    private ElementReference inputElement;
    private ElementReference messagesContainer;
    private CancellationTokenSource cts = new();
    private string? currentSessionId;

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
    /// Gets or sets the session ID for maintaining conversation memory. If null, a new session will be created.
    /// </summary>
    [Parameter]
    public string? SessionId { get; set; }

    /// <summary>
    /// Event callback that is invoked when a session ID is created or retrieved.
    /// </summary>
    [Parameter]
    public EventCallback<string> SessionIdChanged { get; set; }

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
    /// Gets or sets the max tokens.
    /// </summary>
    [Parameter]
    public int? MaxTokens { get; set; }

    /// <summary>
    /// Gets or sets the proxy URL for the AI service.
    /// </summary>
    [Parameter]
    public string? Proxy { get; set; }

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
    /// Gets or sets the maximum number of messages to keep in the chat.
    /// </summary>
    [Parameter]
    public int MaxMessages { get; set; } = 100;

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
    /// Gets the current session ID.
    /// </summary>
    public string? GetSessionId() => currentSessionId;

    /// <summary>
    /// Adds a message to the chat.
    /// </summary>
    /// <param name="content">The message content.</param>
    /// <param name="isUser">Whether the message is from the user.</param>
    /// <returns>The created message.</returns>
    public ChatMessage AddMessage(string content, bool isUser = false)
    {
        var message = new ChatMessage
        {
            Content = content,
            UserId = isUser ? "user" : "system",
            IsUser = isUser,
            Timestamp = DateTime.Now
        };

        Messages.Add(message);

        // Limit the number of messages
        if (Messages.Count > MaxMessages)
        {
            Messages.RemoveAt(0);
        }

        InvokeAsync(StateHasChanged);
        return message;
    }

    /// <summary>
    /// Clears all messages from the chat.
    /// </summary>
    public async Task ClearChat()
    {
        Messages.Clear();

        // Clear the session in the AI service
        if (!string.IsNullOrEmpty(currentSessionId))
        {
            ChatService.ClearSessionAsync(currentSessionId);
        }

        await ChatCleared.InvokeAsync();
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Sends a message programmatically.
    /// </summary>
    /// <param name="content">The message content to send.</param>
    public async Task SendMessage(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || Disabled || IsLoading)
            return;

        // Add user message
        var userMessage = AddMessage(content, true);
        await MessageAdded.InvokeAsync(userMessage);
        await MessageSent.InvokeAsync(content);

        // Clear input
        CurrentInput = string.Empty;
        await InvokeAsync(StateHasChanged);

        // Get AI response
        await GetAIResponse(content);
    }

    /// <summary>
    /// Loads conversation history from the AI service session.
    /// </summary>
    public async Task LoadConversationHistory()
    {
        if (string.IsNullOrEmpty(currentSessionId))
            return;

        var session = await ChatService.GetOrCreateSessionAsync(currentSessionId);

        // Clear current messages
        Messages.Clear();

        // Add messages from session history
        foreach (var message in session.Messages)
        {
            AddMessage(message.Content, message.IsUser);
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task GetAIResponse(string userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput))
            return;

        IsLoading = true;

        await cts.CancelAsync();

        cts = new CancellationTokenSource();

        // Ensure we have a session ID
        if (string.IsNullOrEmpty(currentSessionId))
        {
            currentSessionId = SessionId ?? Guid.NewGuid().ToString();
            await SessionIdChanged.InvokeAsync(currentSessionId);
        }

        // Add assistant message placeholder
        var assistantMessage = AddMessage("", false);
        assistantMessage.IsStreaming = true;

        try
        {
            var response = "";
            await foreach (var token in ChatService.GetCompletionsAsync(userInput, currentSessionId, cts.Token))
            {
                response += token;
                assistantMessage.Content = response;
                await InvokeAsync(StateHasChanged);
            }

            //response = """
            //           <|tool_call_begin|> functions.build_solution:1 <|tool_call_argument_begin|> {"action": "build"} <|tool_call_end|>
            //           """;
            //assistantMessage.Content = response;

            assistantMessage.IsStreaming = false;
            await ResponseReceived.InvokeAsync(response);
            await MessageAdded.InvokeAsync(assistantMessage);

            var functions = ChatService.ParseToolBlock(response);
            if (functions.Count > 0)
            {
                foreach (var tool in functions)
                {
                    var toolAgent = BuiltInAgent.Tools.FirstOrDefault(t => t.Name == tool.Function.Name);
                    if (toolAgent != null)
                    {
                        await toolAgent.ExecuteAsync(JsonUtils.DeserializeParameters(tool.Function.Arguments));
                    }
                }
            }
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

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();

        // Initialize session ID
        currentSessionId = SessionId ?? Guid.NewGuid().ToString();
        if (currentSessionId != SessionId)
        {
            await SessionIdChanged.InvokeAsync(currentSessionId);
        }

        ToolManager.RegisterAllTools();
    }

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();

        // Update session ID if it changed
        if (!string.IsNullOrEmpty(SessionId) && SessionId != currentSessionId)
        {
            currentSessionId = SessionId;
            await SessionIdChanged.InvokeAsync(currentSessionId);

            // Load conversation history for the new session
            await LoadConversationHistory();
        }
    }

    private async Task OnInput(ChangeEventArgs e)
    {
        CurrentInput = e.Value?.ToString() ?? "";
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !e.ShiftKey && JSRuntime != null)
        {
            await JSRuntime.InvokeAsync<string>("Radzen.setInputValue", inputElement, "");
            preventDefault = true;
            await OnSendMessage();
        }
        else
        {
            preventDefault = false;
        }
    }

    private async Task OnSendMessage()
    {
        if (!string.IsNullOrWhiteSpace(CurrentInput))
        {
            await SendMessage(CurrentInput);
        }
    }

    private async Task OnClearChat()
    {
        await ClearChat();
    }

    private async Task OnShowSettings()
    {
        await DialogService.OpenSideAsync<AiSettings>("Settings", options: new SideDialogOptions {
            CloseDialogOnOverlayClick = true,
            Resizable = false,
            Position = DialogPosition.Right,
            MinHeight = 250.0,
            MinWidth = 350.0
        });
    }

    private async Task OnTest()
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
        if (!firstRender && messagesContainer.Context != null && JSRuntime != null)
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

        cts?.Cancel();
        cts?.Dispose();

        GC.SuppressFinalize(this);
    }
}
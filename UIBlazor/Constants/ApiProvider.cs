namespace UIBlazor.Constants;

/// <summary>
/// Defines which OpenAI-compatible API endpoint format to use.
/// </summary>
public enum ApiProvider
{
    /// <summary>
    /// /v1/chat/completions endpoint (OpenAI-compatible, most providers).
    /// </summary>
    ChatCompletions,

    /// <summary>
    /// /v1/responses endpoint (OpenAI Responses API) via the SDK ResponsesClient,
    /// wrapped as IChatClient by Microsoft.Extensions.AI.OpenAIResponsesChatClient.
    /// </summary>
    Responses
}

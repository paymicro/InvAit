using Microsoft.Extensions.AI;

namespace UIBlazor.Services.AgentFramework;

/// <summary>
/// Builds an IChatClient from an OpenAI-compatible provider profile.
/// <see cref="ApiProvider.ChatCompletions"/> returns the own transport <see cref="AfChatCompletionsClient"/>,
/// <see cref="ApiProvider.Responses"/> wraps the Responses API through the SDK ResponsesClient
/// (both: the same endpoint normalization; auth/ExtraHeaders live inside the client for
/// ChatCompletions and in the ExtraHeadersPolicy pipeline for Responses).
/// The DI HttpClient is used as the transport - this preserves
/// <see cref="DynamicEnvironmentHttpMessageHandler"/> (VS Code network proxy, timeouts).
/// </summary>
internal static class AfChatClientFactory
{
    /// <summary>
    /// The provider type selects the wire protocol. ChatCompletions goes through the own
    /// <see cref="AfChatCompletionsClient"/> transport (no SDK, no retries); Responses goes
    /// through the SDK ResponsesClient. Both gateways (single-shot adapter and
    /// tool-invocation loop) support both protocols unchanged.
    /// </summary>
    public static IChatClient Create(ConnectionProfile profile, HttpClient httpClient, Func<int>? httpTimeoutSec = null)
        => profile.ApiProviderType switch
        {
            ApiProvider.Responses => CreateResponsesClient(profile, httpClient, httpTimeoutSec),
            _ => CreateChatClient(profile, httpClient, httpTimeoutSec)
        };

    private static IChatClient CreateChatClient(ConnectionProfile profile, HttpClient httpClient, Func<int>? httpTimeoutSec)
        // Собственный транспорт (не OpenAI SDK): reasoning_content/reasoning маппятся в
        // TextReasoningContent, tool_calls стримятся прогрессивно. Retry не делает —
        // поверх работает app-level retry (RetryHandler). Auth/ExtraHeaders и endpoint
        // ({NormalizeApiBase}/chat/completions) — внутри клиента.
        => new AfChatCompletionsClient(profile, httpClient, httpTimeoutSec);

    private static IChatClient CreateResponsesClient(ConnectionProfile profile, HttpClient httpClient, Func<int>? httpTimeoutSec)
        => new AfResponsesClient(profile, httpClient, httpTimeoutSec);

    /// <summary>
    /// Shared ChatOptions assembly for both gateways (adapter and loop):
    /// temperature, max output tokens, tool mode from the resolved tool list, and the
    /// legacy strict schema flag (NativeToolFunction.Strict = true, additionalProperties: false).
    /// </summary>
    public static ChatOptions BuildChatOptions(ConnectionProfile profile, IReadOnlyList<AITool>? tools)
    {
        var hasTools = tools is { Count: > 0 };

        var options = new ChatOptions
        {
            Temperature = profile.Temperature,
            ToolMode = hasTools ? ChatToolMode.Auto : null,
            Tools = hasTools ? [.. tools!] : null
        };

        if (profile.MaxTokens >= 1000)
        {
            options.MaxOutputTokens = profile.MaxTokens;
        }

        (options.AdditionalProperties ??= [])["strict"] = true;

        return options;
    }
}

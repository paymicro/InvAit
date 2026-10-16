using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace UIBlazor.Services.AgentFramework;

/// <summary>
/// Builds an IChatClient from an OpenAI-compatible provider profile.
/// <see cref="ApiProvider.ChatCompletions"/> wraps the Chat Completions client,
/// <see cref="ApiProvider.Responses"/> wraps the Responses API through the SDK ResponsesClient
/// (both: the same endpoint normalization, api-key placeholder and ExtraHeaders pipeline).
/// The DI HttpClient is used as the transport - this preserves
/// <see cref="DynamicEnvironmentHttpMessageHandler"/> (VS Code network proxy, timeouts).
/// </summary>
internal static class AfChatClientFactory
{
    /// <summary>
    /// The provider type selects the wire protocol; the common pipeline setup (transport,
    /// headers policy, key placeholder) is identical for both branches, so both gateways
    /// (single-shot adapter and tool-invocation loop) support both protocols unchanged.
    /// </summary>
    public static IChatClient Create(ConnectionProfile profile, HttpClient httpClient)
        => profile.ApiProviderType switch
        {
            ApiProvider.Responses => CreateResponsesClient(profile, httpClient),
            _ => CreateChatClient(profile, httpClient)
        };

    private static IChatClient CreateChatClient(ConnectionProfile profile, HttpClient httpClient)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(ConnectionProfile.NormalizeApiBase(profile.Endpoint)),
            Transport = new HttpClientPipelineTransport(httpClient)
        };
        options.AddPolicy(new ExtraHeadersPolicy(profile), PipelinePosition.PerTry);

        return new ChatClient(profile.Model, new ApiKeyCredential(GetApiKeyPlaceholder(profile)), options)
            .AsIChatClient();
    }

    /// <summary>
    /// Responses protocol: the SDK ResponsesClient is wrapped by the ready-made MEAI bridge
    /// <see cref="Microsoft.Extensions.AI.OpenAIClientExtensions.AsIChatClient(OpenAI.Responses.ResponsesClient, string)"/>
    /// (message/tool/usage conversion included, streaming and non-streaming), so the
    /// downstream gateways stay protocol-agnostic. The default model comes from the profile.
    /// </summary>
#pragma warning disable OPENAI001 // OpenAI Responses bridge (Microsoft.Extensions.AI.OpenAI)
    private static IChatClient CreateResponsesClient(ConnectionProfile profile, HttpClient httpClient)
    {
        var options = new ResponsesClientOptions
        {
            Endpoint = new Uri(ConnectionProfile.NormalizeApiBase(profile.Endpoint)),
            Transport = new HttpClientPipelineTransport(httpClient)
        };
        options.AddPolicy(new ExtraHeadersPolicy(profile), PipelinePosition.PerTry);

        return new ResponsesClient(new ApiKeyCredential(GetApiKeyPlaceholder(profile)), options)
            .AsIChatClient(profile.Model);
    }
#pragma warning restore OPENAI001

    /// <summary>
    /// ApiKeyCredential does not accept empty strings; local/proxy servers ignore api keys,
    /// so the placeholder "-" is sent where the legacy code would not have sent Authorization at all.
    /// </summary>
    private static string GetApiKeyPlaceholder(ConnectionProfile profile)
        => string.IsNullOrWhiteSpace(profile.ApiKey) ? "-" : profile.ApiKey;

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
            Temperature = (float?)profile.Temperature,
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

/// <summary>
/// Overrides Authorization when the profile uses a custom key header
/// and adds ExtraHeaders on every outgoing request.
/// </summary>
internal sealed class ExtraHeadersPolicy(ConnectionProfile profile) : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message.Request.Headers);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message.Request.Headers);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    private void Apply(PipelineRequestHeaders headers)
    {
        var hasCustomAuthHeader = !string.IsNullOrWhiteSpace(profile.ApiKeyHeader)
            && !string.Equals(profile.ApiKeyHeader, "Authorization", StringComparison.OrdinalIgnoreCase);
        var hasApiKey = !string.IsNullOrEmpty(profile.ApiKey);

        if (hasCustomAuthHeader && hasApiKey)
        {
            headers.Set(profile.ApiKeyHeader, profile.ApiKey);
        }

        foreach (var header in profile.ExtraHeaders.Where(h => !string.IsNullOrEmpty(h.Name)))
        {
            headers.Set(header.Name, header.Value);
        }
    }
}

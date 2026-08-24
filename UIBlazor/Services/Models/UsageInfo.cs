namespace UIBlazor.Services.Models;

public class UsageInfo
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }

    /// <summary>
    /// OpenAI-compatible details block. Reasoning models (DeepSeek R1, GLM, o-series, ...)
    /// report the number of thinking tokens here. Null when the provider does not send it.
    /// </summary>
    [JsonPropertyName("completion_tokens_details")]
    public CompletionTokensDetails? CompletionDetails { get; set; }

    /// <summary>Reasoning tokens if the provider reports them, otherwise 0.</summary>
    [JsonIgnore]
    public int ReasoningTokens => CompletionDetails?.ReasoningTokens ?? 0;
}

public class CompletionTokensDetails
{
    [JsonPropertyName("reasoning_tokens")]
    public int ReasoningTokens { get; set; }
}

namespace UIBlazor.Tests.Services;

public class CompletionsResultTests
{
    [Fact]
    public void VisibleCompletionTokens_CompletionLessThanReasoning_ClampsToZero()
    {
        var result = new CompletionsResult { CompletionTokens = 10, ReasoningTokens = 40 };

        Assert.Equal(0, result.VisibleCompletionTokens);
    }

    [Fact]
    public void Reset_ClearsReasoningTokens()
    {
        var result = new CompletionsResult { CompletionTokens = 100, ReasoningTokens = 40 };

        result.Reset();

        Assert.Equal(0, result.CompletionTokens);
        Assert.Equal(0, result.ReasoningTokens);
    }
}

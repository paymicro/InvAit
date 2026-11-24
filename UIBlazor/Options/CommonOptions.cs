using System.Globalization;

namespace UIBlazor.Options;

public class CommonOptions : BaseOptions
{
    /// <summary>
    /// Лимит одного вызова инструмента, мс. Едет в payload запроса (bash — как <c>timeoutMs</c>,
    /// MCP — так же), поэтому это не «сколько ждать в UI», а настоящий лимит команды на хосте.
    /// Клиент при этом ждёт <see cref="ToolTimeout.ClientWait"/> — строго дольше, иначе результат
    /// убитой команды теряется.
    /// </summary>
    public int ToolTimeoutMs { get; set => SetIfChanged(ref field, value); } = ToolTimeout.DefaultCommandMs;

    /// <summary>
    /// Idle window of an LLM request, in seconds. The window is re-armed after every delivered
    /// chunk, so it bounds the GAP between chunks (connect + first byte, then chunk-to-chunk),
    /// not the total generation time — a long answer with steady chunks is never cut.
    /// Non-streaming profiles have one body-read, so there the window is a total budget.
    /// <para>
    /// <c>0</c> (or any negative value) disables the timeout entirely, the same convention
    /// <see cref="ProfileOptions.ActiveProfile"/>&#39;s <c>TokensToCompress</c> uses for
    /// "unlimited". This matters because the window is the ONLY timeout of an LLM request
    /// (<see cref="UIBlazor.Services.AgentFramework.AfCompletionsAdapter"/> owns it; the
    /// transport clients get none), so there is no lower layer to fall back on.
    /// </para>
    /// </summary>
    public int HttpTimeoutSec { get; set => SetIfChanged(ref field, value); } = 100;

    public bool ShowMessageTimings { get; set => SetIfChanged(ref field, value); } = true;

    public string Culture { get; set => SetIfChanged(ref field, value); } = CultureInfo.CurrentCulture.Name;

    /// <summary>
    /// Retry budget of ONE turn (main agent) or one branch run (sub-agent), counted in ATTEMPTS.
    /// <para>
    /// Only failures that can succeed on a repeat spend it: <see cref="Models.LlmApiException"/>
    /// with <c>IsRetryable</c> true (no status, <c>2xx</c>, <c>5xx</c>, <c>408</c>/<c>409</c>/
    /// <c>425</c>/<c>429</c>). A rejected request (<c>400</c>, <c>401</c>, <c>403</c>, <c>404</c>
    /// ...) and an exception that is not an <see cref="Models.LlmApiException"/> (a bug in our own
    /// code) fail on the first attempt — otherwise the budget turns into minutes of waiting for an
    /// answer that cannot change.
    /// </para>
    /// </summary>
    public int MaxRetries { get; set => SetIfChanged(ref field, value); } = 10;

    public ContentFilterSettings ContentFilter { get; set => SetIfChanged(ref field, value); } = new();
}

using System.ComponentModel;

namespace UIBlazor.Services.Interfaces;

public interface IChatService : IDisposable
{
    ConversationSession Session { get; }

    /// <summary>
    /// Первоначальная подписка на для <seealso cref="SessionChanged"/> 
    /// </summary>
    void Initialize();

    /// <summary>
    /// Событие создания новой сессии
    /// или изменения параметров <seealso cref="ConversationSession"/> с SetIfChanged (только режим)
    /// </summary>
    event PropertyChangedEventHandler? SessionChanged;

    Task<List<SessionSummary>> GetRecentSessionsAsync(int count);

    /// <summary>
    /// Нужно ли сжимать ПИННУТУЮ сессию прогона. Прогон никогда не читает мутабельное
    /// свойство <see cref="Session"/>: он принадлежит той сессии, в которой стартовал.
    /// </summary>
    bool NeedsCompression(ConversationSession targetSession);

    /// <summary>
    /// Streams exactly ONE LLM turn for the pinned session: the request history is rebuilt from
    /// the session at call time (validated by <see cref="ConversationSession.BuildWire"/>), so
    /// compression / edits / deletions / interjects / mode switches applied between turns take
    /// effect immediately. Tool execution is NOT performed here — tool calls flow outward through
    /// <paramref name="resultCapture"/> and the caller runs the approval/execution path.
    /// </summary>
    /// <param name="targetSession">Pinned run session.</param>
    /// <param name="live">Message the caller is about to write into; excluded by reference.</param>
    /// <param name="tools">Explicit tool list (sub-agent branch); null resolves the session's current mode.</param>
    /// <param name="resultCapture">Per-turn state accumulator.</param>
    IAsyncEnumerable<ChatDelta> GetTurnCompletionsAsync(
        ConversationSession targetSession,
        VisualChatMessage? live,
        IReadOnlyList<Tool>? tools,
        CompletionsResult resultCapture,
        CancellationToken cancellationToken);

    Task LoadSessionAsync(string id);

    Task DeleteSessionAsync(string id);

    Task SaveSessionAsync();

    Task NewSessionAsync();

    /// <summary>
    /// Сжатие контекста основной сессии: голова и хвост истории остаются дословно,
    /// середина заменяется сводкой от LLM на своём месте (живые сообщения запуска не трогаются).
    /// Состояние LLM-вызова записывается в <paramref name="resultCapture"/>.
    /// </summary>
    IAsyncEnumerable<ChatDelta> CompressSessionAsync(CompletionsResult resultCapture, CancellationToken cancellationToken);

    /// <summary>
    /// Сжатие завершилось и история переписана (сводка вставлена). Поднимается для ЛЮБОГО пути
    /// сжатия — ручного (/compress), mid-run гейта и веток сабагентов:
    /// в главном чате нет своего индикатора для невидимых путей.
    /// </summary>
    event Action<CompressionReport>? CompressionCompleted;

    /// <summary>
    /// Сжатие началось: стрим сводки уходит в пользу сервиса (mid-run гейт вообще его глотает),
    /// поэтому без этого сигнала пользователь видит просто замерший чат.
    /// </summary>
    event Action<ConversationSession>? CompressionStarted;

    /// <summary>
    /// Сжатие контекста указанной сессии (например, ветки субагента): голова и хвост истории
    /// остаются дословно, середина заменяется сводкой от LLM на своём месте.
    /// Состояние LLM-вызова записывается в <paramref name="resultCapture"/>.
    /// </summary>
    IAsyncEnumerable<ChatDelta> CompressSessionAsync(ConversationSession targetSession, CompletionsResult resultCapture, CancellationToken cancellationToken);

    /// <summary>
    /// Получение стандартного ответа.
    /// Состояние LLM-вызова (model, usage, tool_calls, finish_reason, error)
    /// записывается в  <paramref name="resultCapture"/>.
    /// </summary>
    IAsyncEnumerable<ChatDelta> GetCompletionsAsync(CompletionsResult resultCapture, CancellationToken cancellationToken);

    /// <summary>
    /// Получение списка моделей по API
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="HttpRequestException"></exception>
    /// <exception cref="JsonException"></exception>
    Task<AiModelList> GetModelsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Wrapper over the <seealso cref="ChatDelta"/> stream: applies the deltas of exactly ONE
    /// LLM turn to the given message, raises the content/state callbacks, counts tokens and
    /// ALWAYS closes the message (<c>IsStreaming = false</c> + final timings), cancellation and
    /// failure included.
    /// <para>
    /// The turn cycle belongs to the caller: one <c>GetTurnCompletionsAsync</c> → one
    /// <c>ProcessStreamAsync</c>.
    /// </para>
    /// </summary>
    /// <param name="message">Сообщение с которым работаем</param>
    /// <param name="deltas">Функция получения <seealso cref="ChatDelta"/></param>
    /// <param name="onContentUpdate">Обновление <see cref="VisualChatMessage.Content"/></param>
    /// <param name="onStateChange">Внесены изменения в <see cref="VisualChatMessage"/></param>
    /// <param name="resultCapture">Контейнер для состояния LLM-вызова (accumulated tool calls, usage и т.д.)</param>
    Task ProcessStreamAsync(
        VisualChatMessage message,
        IAsyncEnumerable<ChatDelta> deltas,
        Action<string>? onContentUpdate,
        Action<List<ToolCall>> onToolCallsUpdate,
        Action? onStateChange,
        CompletionsResult resultCapture,
        CancellationToken cancellationToken);
}

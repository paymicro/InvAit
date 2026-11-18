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
    /// Нужно ли сжимать сессию
    /// </summary>
    bool NeedCompression { get; }

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
    /// сжатия — ручного (/compact), mid-run гейта и веток сабагентов:
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
    /// Обертка над стримингом <seealso cref="ChatDelta"/>.
    /// Вызывает события по обновлению контента и считает токены.
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

    /// <summary>
    /// Agent-loop runs (FunctionInvokingChatClient): same as the base <see cref="ProcessStreamAsync(VisualChatMessage, ...)"/>;
    /// the factory is invoked when the agent run starts a new assistant turn and its returned
    /// message becomes the current streaming target.
    /// </summary>
    Task ProcessStreamAsync(
        VisualChatMessage message,
        IAsyncEnumerable<ChatDelta> deltas,
        Action<string>? onContentUpdate,
        Action<List<ToolCall>> onToolCallsUpdate,
        Action? onStateChange,
        CompletionsResult resultCapture,
        CancellationToken cancellationToken,
        Func<VisualChatMessage>? onNewTurn);
}

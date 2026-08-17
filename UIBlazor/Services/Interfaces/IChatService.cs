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
    /// Сжатие контекста. Остается 2 сообщения + сжатое сообщение.
    /// Состояние LLM-вызова записывается в <paramref name="resultCapture"/>.
    /// </summary>
    IAsyncEnumerable<ChatDelta> CompressSessionAsync(CompletionsResult resultCapture, CancellationToken cancellationToken);

    /// <summary>
    /// Получение стандартного ответа.
    /// Состояние LLM-вызова (model, usage, tool_calls, finish_reason, error)
    /// записывается в  <paramref name="resultCapture"/>.
    /// </summary>
    IAsyncEnumerable<ChatDelta> GetCompletionsAsync(CompletionsResult resultCapture, CancellationToken cancellationToken);

    /// <summary>
    /// Получение ответа для sub-agent с произвольным системным промптом и набором инструментов.
    /// Использует переданную сессию вместо основной.
    /// Состояние записывается в <paramref name="resultCapture"/>.
    /// </summary>
    IAsyncEnumerable<ChatDelta> GetCompletionsForSubAgentAsync(
        ConversationSession session,
        string systemPrompt,
        IEnumerable<Tool> enabledTools,
        CompletionsResult resultCapture,
        CancellationToken cancellationToken);

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
}

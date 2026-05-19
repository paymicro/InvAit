using System.ComponentModel;
using System.Runtime.CompilerServices;
using UIBlazor.Services.Models;

namespace UIBlazor.Services;

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
    /// Сжатие контекста. Остается 2 сообщения + сжатое сообщение
    /// </summary>
    IAsyncEnumerable<ChatDelta> CompressSessionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Нужно ли сжимать сессию
    /// </summary>
    bool NeedCompression { get; }

    Task LoadSessionAsync(string id);

    Task DeleteSessionAsync(string id);

    Task SaveSessionAsync();

    Task NewSessionAsync();

    string? LastCompletionsModel { get; }

    string? FinishReason { get; }

    public string? LastError { get; }

    public UsageInfo? LastUsage { get; }

    IAsyncEnumerable<ChatDelta> GetCompletionsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Получение списка моделей по API
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="HttpRequestException"></exception>
    /// <exception cref="JsonException"></exception>
    Task<AiModelList> GetModelsAsync(CancellationToken cancellationToken);
}

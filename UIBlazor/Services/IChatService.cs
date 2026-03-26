using UIBlazor.Services.Models;

namespace UIBlazor.Services;

public interface IChatService
{
    ConversationSession Session { get; }

    event Action<string>? OnSessionChanged;

    Task<List<SessionSummary>> GetRecentSessionsAsync(int count);

    Task LoadSessionAsync(string id);

    Task DeleteSessionAsync(string id);

    Task SaveSessionAsync();

    Task NewSessionAsync();

    string? LastCompletionsModel { get; }

    string? FinishReason { get; }

    public string? LastError { get; }

    IAsyncEnumerable<ChatDelta> GetCompletionsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Получение списка моделей по API
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="HttpRequestException"></exception>
    /// <exception cref="JsonException"></exception>
    Task<AiModelList> GetModelsAsync(CancellationToken cancellationToken);
}

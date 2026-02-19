namespace UIBlazor.Services;

public interface IMessageParser
{
    /// <summary>
    /// Updates the segments of a visual chat message based on incoming text delta.
    /// </summary>
    /// <param name="delta">The incoming text chunk.</param>
    /// <param name="assistant">The assistant message being updated.</param>
    /// <param name="isHistory">Whether we are parsing history.</param>
    void UpdateSegments(string delta, VisualChatMessage assistant, bool isHistory = false);

    /// <summary>
    /// Парсим рагументы тулзы перед вызовом
    /// </summary>
    /// <param name="toolName">Имя тулзы</param>
    /// <param name="toolLines">Параметры по линиям</param>
    Dictionary<string, object> Parse(string toolName, List<string> toolLines);
}

using UIBlazor.Agents;

namespace UIBlazor.VS;

public interface IVsBridge
{
    Task<string> ReadOpenFileAsync();

    Task<string> GetSelectedTextAsync();

    Task<List<string>> GetOpenDocumentsAsync();

    Task InsertTextAtPositionAsync(string filePath, int line, int column, string text);

    Task<VsToolResult> BuildSolutionAsync(string action);
}

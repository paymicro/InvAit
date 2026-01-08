namespace Shared.Contracts;

public interface IVsBridge
{
    Task<string> GetActiveDocumentContentAsync();
    Task<string> GetSelectedTextAsync();
    Task<List<string>> GetOpenDocumentsAsync();
    Task InsertTextAtPositionAsync(string filePath, int line, int column, string text);
}

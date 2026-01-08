using Microsoft.JSInterop;
using Shared.Contracts;

namespace UIBlazor.VS;

public class VsBridgeProxy : IVsBridge
{
    private readonly IJSRuntime _js;

    public VsBridgeProxy(IJSRuntime js) => _js = js;

    public async Task<string> GetActiveDocumentContentAsync()
    {
        var request = new VsRequest { Action = "getActiveDocumentContent" };
        var tcs = new TaskCompletionSource<string>();

        // Здесь нужно реализовать механизм ожидания ответа по correlationId
        // (например, словарь TaskCompletionSource + OnResponse callback)

        await _js.InvokeVoidAsync("postVsMessage", request);
        return await tcs.Task;
    }

    public Task<List<string>> GetOpenDocumentsAsync()
    {
        return Task.FromResult(new List<string>());
    }

    public Task<string> GetSelectedTextAsync()
    {
        return Task.FromResult("");
    }

    public Task InsertTextAtPositionAsync(string filePath, int line, int column, string text)
    {
        return Task.FromResult("");
    }
}
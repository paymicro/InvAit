using System.Text.Json;
using Microsoft.JSInterop;

namespace UIBlazor.Services.Settings;

public class LocalStorageService(IJSRuntime js) : ILocalStorageService
{
    public async Task SetItemAsync<T>(string key, T value)
    {
        var json = JsonSerializer.Serialize(value);
        await js.InvokeVoidAsync("localStorage.setItem", key, json);
    }

    public async Task<T?> GetItemAsync<T>(string key)
    {
        var json = await js.InvokeAsync<string?>("localStorage.getItem", key);
        return json == null ? default : JsonSerializer.Deserialize<T>(json);
    }

    public async Task RemoveItemAsync(string key)
        => await js.InvokeAsync<string?>("localStorage.removeItem", key);

    public async Task<List<string>> GetAllKeysAsync()
    {
        return await js.InvokeAsync<List<string>>("eval", "Object.keys(localStorage)");
    }
}
using Microsoft.JSInterop;

namespace UIBlazor.Services.Settings;

public class LocalStorageService(IJSRuntime js, ILogger<LocalStorageService> logger) : ILocalStorageService
{
    public async Task SetItemAsync<T>(string key, T value)
    {
        var json = JsonSerializer.Serialize(value);
        await js.InvokeVoidAsync("localStorage.setItem", key, json);
    }

    public async Task<T?> TryGetItemAsync<T>(string key)
    {
        try
        {
            var json = await js.InvokeAsync<string?>("localStorage.getItem", key);
            return json == null ? default : JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            logger.LogError("Error getting item '{key}': {message}", key, ex.Message);
            return default;
        }
    }

    public async Task RemoveItemAsync(string key)
        => await js.InvokeAsync<string?>("localStorage.removeItem", key);

    public async Task<List<string>> GetAllKeysAsync()
    {
        return await js.InvokeAsync<List<string>>("eval", "Object.keys(localStorage)");
    }
}
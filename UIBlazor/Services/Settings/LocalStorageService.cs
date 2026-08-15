using Microsoft.JSInterop;

namespace UIBlazor.Services.Settings;

public class LocalStorageService(IJSRuntime js, ILogger<LocalStorageService> logger) : ILocalStorageService
{
    public async Task SetItemAsync<T>(string key, T value)
    {
        var json = JsonSerializer.Serialize(value);
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", key, json);
        }
        catch (JSException ex) when (ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("localStorage quota exceeded for key '{key}'. Attempting cleanup...", key);
            await TryCleanupOldSessionsAsync(key);
            // Retry after cleanup
            try
            {
                await js.InvokeVoidAsync("localStorage.setItem", key, json);
            }
            catch (JSException ex2)
            {
                logger.LogError("Failed to save '{key}' even after cleanup: {message}", key, ex2.Message);
            }
        }
    }

    /// <summary>
    /// Remove old session keys to free up localStorage space.
    /// Keeps only the most recent session (the one being saved).
    /// </summary>
    private async Task TryCleanupOldSessionsAsync(string keepKey)
    {
        try
        {
            var keys = await js.InvokeAsync<List<string>>("eval", "Object.keys(localStorage)");
            var sessionKeys = keys
                .Where(k => k.StartsWith("session_") && k != keepKey)
                .OrderByDescending(k => k)
                .ToList();

            // Remove all other sessions to free space
            foreach (var k in sessionKeys)
            {
                await js.InvokeVoidAsync("localStorage.removeItem", k);
                logger.LogInformation("Removed old session '{key}' during quota cleanup", k);
            }
        }
        catch (Exception ex)
        {
            logger.LogError("Error during localStorage cleanup: {message}", ex.Message);
        }
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
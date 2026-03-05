namespace UIBlazor.Services.Settings;

public interface ILocalStorageService
{
    Task SetItemAsync<T>(string key, T value);

    Task<T?> TryGetItemAsync<T>(string key);

    Task RemoveItemAsync(string key);

    Task<List<string>> GetAllKeysAsync();
}
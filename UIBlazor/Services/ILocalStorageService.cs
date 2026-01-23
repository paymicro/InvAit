namespace UIBlazor.Services;

public interface ILocalStorageService
{
    Task SetItemAsync<T>(string key, T value);

    Task<T?> GetItemAsync<T>(string key);

    Task RemoveItemAsync(string key);

    Task<List<string>> GetAllKeysAsync();
}
namespace UIBlazor.Services;

public interface IFileContentService
{
    Task<string?> ReadFileContentAsync(string filePath);
}

public class FileContentService : IFileContentService
{
    private readonly IVsBridge _vsBridge;

    public FileContentService(IVsBridge vsBridge)
    {
        _vsBridge = vsBridge;
    }

    public async Task<string?> ReadFileContentAsync(string filePath)
    {
        try
        {
            return "content";

            var args = new Dictionary<string, object>
            {
                { "param1", filePath }
            };
            var response = await _vsBridge.ExecuteToolAsync(BuiltInToolEnum.ReadFiles, args);
            
            // Парсим ответ и извлекаем содержимое файла

            // return response;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading file {filePath}: {ex.Message}");
            return null;
        }
    }
}

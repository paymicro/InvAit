namespace UIBlazor.Utils;

public static class ConsoleUtils
{
    public static async Task WriteLineAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            await Console.Out.WriteLineAsync(message.AsMemory(), cancellationToken);
        }
        catch
        {
            // логирование не имеет права крешить
        }
    }
}

using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;

namespace InvAit.Utils;

internal static class Logger
{
    private static OutputWindowPane _pane;

    public static async Task InitializeAsync()
    {
        // Создаем или получаем панель
        _pane = await VS.Windows.CreateOutputWindowPaneAsync(Vsix.Name);
    }

    public static void Log(string message, string level = "INFO")
    {
        LogAsync(message, level).FireAndForget();
    }

    public static Task LogAsync(string message, string level = "INFO")
    {
        _pane?.WriteLineAsync(GetMessage(message, level)).FireAndForget();

        // Если это ошибка, можно автоматически активировать панель, чтобы пользователь её увидел
        if (level == "ERROR")
        {
            _pane?.ActivateAsync().FireAndForget();
        }

        return Task.CompletedTask;
    }

    private static string GetMessage(string message, string level)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        return $"[{timestamp}] [{level}] {message}";
    }
}

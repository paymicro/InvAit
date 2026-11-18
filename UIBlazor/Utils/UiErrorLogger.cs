using Microsoft.JSInterop;
using Radzen;

namespace UIBlazor.Utils;

/// <summary>
/// Централизованный логгер ошибок UI. Каждый вызов fire-and-forget публикует <see cref="LogEntry"/>:
/// 1) в Console (браузерная JS-консоль — devtools), через async API;
/// 2) хосту (VS Output / VS Code OutputChannel) через <see cref="BasicEnum.LogError"/> —
///    без VsResponse и без _pendingRequests (как UIReady/SkipSSL).
/// Синхронная оболочка <see cref="Log"/> не крешит вызывающий код: все сбойные ветки глотаются.
/// </summary>
public class UiErrorLogger
{
    private readonly IJSRuntime _jsRuntime;

    public UiErrorLogger(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public void Log(string source, string message, Exception? ex = null, string level = LogEntry.ErrorLevel)
    {
        var entry = new LogEntry
        {
            Level = level,
            Source = source,
            Message = message,
            Exception = ex is null ? null : $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}",
        };

        // Fire-and-forget: вызывающий код (компоненты) не ждёт и не видит сбоев логирования.
        _ = PublishAsync(entry);
    }

    public void LogError(string source, string message, Exception? ex = null) => Log(source, message, ex, LogEntry.ErrorLevel);
    public void LogWarning(string source, string message, Exception? ex = null) => Log(source, message, ex, LogEntry.WarnLevel);

    private async Task PublishAsync(LogEntry entry)
    {
        // 1) Браузерная консоль — всегда.
        try
        {
            await Console.Out.WriteLineAsync(Format(entry));
        }
        catch
        {
            // Консоль недоступна — логирование не имеет права крешить UI.
        }

        // 2) Хост-лог.
        try
        {
            await _jsRuntime.InvokeVoidAsync("postVsMessage", new VsRequest
            {
                Action = BasicEnum.LogError,
                Payload = JsonUtils.SerializeCompact(entry),
            });
        }
        catch (Exception ex)
        {
            // Мост может быть ещё не инициализирован или JS недоступен — не крешим UI.
            try
            {
                await Console.Out.WriteLineAsync($"[UiErrorLogger] Failed to post log entry to host: {ex.Message}");
            }
            catch
            {
                // Даже консоль недоступна — окончательно глотаем.
            }
        }
    }

    private static string Format(LogEntry entry)
        => $"[{entry.Level}] [{entry.Source}] {entry.Message}{(entry.Exception is null ? string.Empty : "\n" + entry.Exception)}";
}

/// <summary>
/// Хелпер над Radzen <see cref="NotificationService"/>: логирует через <see cref="UiErrorLogger"/>
/// ДО показа notification — покрывает требование «логировать все ошибки, которые отображаются
/// NotificationService» в одной точке.
/// </summary>
public static class UiErrorLoggerNotifications
{
    /// <summary>Логирует и показывает error-notification (Summary + Detail).</summary>
    public static void NotifyError(this NotificationService notifications, UiErrorLogger logger,
        string summary, string? detail = null, string source = "Notification", Exception? ex = null,
        int duration = 10_000, bool showProgress = true)
    {
        logger.LogError(source, string.IsNullOrEmpty(detail) ? summary : $"{summary}: {detail}", ex);
        notifications.Notify(new NotificationMessage
        {
            Severity = NotificationSeverity.Error,
            Summary = summary,
            Detail = detail ?? string.Empty,
            Duration = duration,
            ShowProgress = showProgress,
        });
    }

    /// <summary>Логирует (WARN) и показывает warning-notification.</summary>
    public static void NotifyWarning(this NotificationService notifications, UiErrorLogger logger,
        string summary, string? detail = null, string source = "Notification", Exception? ex = null,
        int duration = 10_000, bool showProgress = true)
    {
        logger.LogWarning(source, string.IsNullOrEmpty(detail) ? summary : $"{summary}: {detail}", ex);
        notifications.Notify(new NotificationMessage
        {
            Severity = NotificationSeverity.Warning,
            Summary = summary,
            Detail = detail ?? string.Empty,
            Duration = duration,
            ShowProgress = showProgress,
        });
    }
}

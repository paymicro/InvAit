using System.Collections.Concurrent;
using Microsoft.JSInterop;

namespace UIBlazor.VS;

public class VsBridge : IVsBridge, IDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private readonly ICommonSettingsProvider _commonOptions;
    private readonly IContextService _vsCodeContextService;
    private DotNetObjectReference<VsBridge> _dotNetRef;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<VsResponse>> _pendingRequests;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _isInitialized;

    public VsBridge(IJSRuntime jsRuntime,
         ICommonSettingsProvider commonSettingsProvider,
         IContextService vsCodeContextService)
    {
        _jsRuntime = jsRuntime;
        _commonOptions = commonSettingsProvider;
        _vsCodeContextService = vsCodeContextService;
        _pendingRequests = new ConcurrentDictionary<string, TaskCompletionSource<VsResponse>>();
    }

    public async Task InitializeAsync()
    {
        // Fast path — already initialized
        if (_isInitialized)
            return;

        await _initLock.WaitAsync();
        try
        {
            // Double-check inside the lock
            if (_isInitialized)
                return;

            _dotNetRef = DotNetObjectReference.Create(this);
            var result = await _jsRuntime.InvokeAsync<string>("setVsBridgeHandler", _dotNetRef);
            _isInitialized = result == "OK";

            if (_isInitialized)
            {
                // Notify Host that we are ready to receive messages (e.g. initial context)
                await _jsRuntime.InvokeVoidAsync("postVsMessage", new VsRequest { Action = BasicEnum.UIReady });
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<VsToolResult> ExecuteToolAsync(string name, string? args, CancellationToken cancellationToken = default)
    {
        var request = new VsRequest
        {
            Action = name,
            Payload = args
        };

        var response = await SendRequestAsync(request, cancellationToken);

        if (!response.Success)
        {
            // Ожидаемый отказ инструмента (файл не найден, нет прав чтения и т.п.) — это
            // РЕЗУЛЬТАТ, а не сбой приложения. Логируем его уровнем WARN в обычные каналы
            // (devtools-консоль через Console.WriteLine + хост-лог), но никогда не пишем
            // в Console.Error: Blazor WASM реагирует на error-канал показом плашки
            // #blazor-error-ui, и ожидаемый отказ выглядел бы как падение приложения.
            _ = PostLogAsync(new LogEntry
            {
                Level = LogEntry.WarnLevel,
                Source = "VsBridge",
                Message = $"Request '{request.Action}' failed: {response.Payload}",
            });
        }

        return Convert(request, response);
    }

    private VsToolResult Convert(VsRequest vsRequest, VsResponse vsResponse)
    {
        // Бинарный контракт VsResponse: дискриминатор — Success, отдельного канала ошибки нет.
        // При отказе весь Payload — причина, поэтому ErrorMessage заполняется из него ЖЕ.
        // Пустым он быть не должен: ToolResult.Convert показывает LLM именно ErrorMessage,
        // и пустая строка превратила бы отказ в «успешный» пустой результат инструмента.
        var payload = vsResponse.Payload ?? string.Empty;
        var errorMessage = vsResponse.Success
            ? string.Empty
            : (string.IsNullOrEmpty(payload) ? "Request failed" : payload);

        return new VsToolResult
        {
            ErrorMessage = errorMessage,
            Name = vsRequest.Action,
            Result = payload,
            Success = vsResponse.Success
        };
    }

    private async Task<VsResponse> SendRequestAsync(VsRequest request, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync();

        var tcs = new TaskCompletionSource<VsResponse>();
        _pendingRequests.TryAdd(request.CorrelationId, tcs);

        try
        {
            // Отправляем запрос
            var result = await _jsRuntime.InvokeAsync<string>("postVsMessage", cancellationToken: cancellationToken, request);

            if (result != "OK")
            {
                return new VsResponse
                {
                    Success = false,
                    Payload = "WebView2 API not found."
                };
            }

            var timeOut = TimeSpan.FromMilliseconds(_commonOptions.Current.ToolTimeoutMs);
#if DEBUG
            // Дебаг не быстрый)
            timeOut = TimeSpan.FromDays(1);
#endif
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

            using var timeoutCts = new CancellationTokenSource(timeOut);
            timeoutCts.Token.Register(() =>
                tcs.TrySetException(new TimeoutException("Request timed out")));

            // Ждем ответа
            return await tcs.Task;
        }
        catch (Exception ex)
        {
            // Здесь остаются только настоящие аварии транспорта (таймаут, отмена, сбой JS).
            // Пишем в обычный консольный канал, а не в Console.Error: иначе Blazor WASM
            // показывает плашку #blazor-error-ui принудительно. Хост-лог получает запись
            // уровнем ERROR — там она и должна быть видна.
            // Запись в консоль не может вылететь из catch и заменить задуманный error-ответ.
            try
            {
                await Console.Out.WriteLineAsync($"[VsBridge] Request '{request.Action}' failed: {ex.Message}");
            }
            catch
            {
                // Даже консоль недоступна — окончательно глотаем.
            }
            _ = PostLogAsync(new LogEntry
            {
                Level = LogEntry.ErrorLevel,
                Source = "VsBridge",
                Message = $"Request '{request.Action}' failed",
                Exception = $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}",
            });

            return new VsResponse
            {
                Success = false,
                Payload = $"Transport failure: {ex.Message}"
            };
        }
        finally
        {
            // Удаляем из ожидающих запросов
            _pendingRequests.TryRemove(request.CorrelationId, out _);
        }
    }

    [JSInvokable("HandleVsMessage")]
    public Task HandleVsMessageAsync(VsMessage message)
    {
        switch (message.Action)
        {
            case "UpdateCodeContext":
                if (!string.IsNullOrEmpty(message.Payload))
                {
                    try
                    {
                        var context = JsonUtils.Deserialize<VsContext>(message.Payload);
                        if (context != null)
                        {
                            _vsCodeContextService.UpdateContext(context);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error deserializing VsCodeContext: {ex.Message}");
                    }
                }
                break;
            default:
                Console.WriteLine($"Unknown message action: {message.Action}");
                break;
        }

        return Task.CompletedTask;
    }

    [JSInvokable("HandleVsResponse")]
    public Task HandleVsResponseAsync(VsResponse response)
    {
        try
        {
            if (string.IsNullOrEmpty(response.CorrelationId))
            {
                Console.WriteLine("Invalid response received");
                return Task.CompletedTask;
            }

            if (_pendingRequests.TryRemove(response.CorrelationId, out var tcs))
            {
                // Ответ хоста — всегда результат, даже когда Success = false: отказ
                // инструмента (файл не найден и т.п.) не является исключением транспорта.
                // Исключения в мосте остаются только для аварий: таймаут и отмена.
                tcs.TrySetResult(response);
            }
            else
            {
                Console.WriteLine($"No pending request found for correlationId: {response.CorrelationId}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling VS response: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private async Task EnsureInitializedAsync()
    {
        await InitializeAsync();
    }

    /// <summary>
    /// Fire-and-forget лог-запись в хост (VS Output / VS Code OutputChannel) через
    /// BasicEnum.LogError — как UIReady/SkipSSL, без регистрации в _pendingRequests
    /// и без ожидания VsResponse. Сбой глотается (лог не имеет права крешить транспорт).
    /// </summary>
    internal async Task PostLogAsync(LogEntry entry)
    {
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
            // Не Console.Error: сбой доставки лога не должен поднимать плашку blazor-error-ui.
            await Console.Out.WriteLineAsync($"[VsBridge] Failed to post log entry: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _dotNetRef?.Dispose();
        _initLock.Dispose();

        // Отменяем все ожидающие запросы
        foreach (var tcs in _pendingRequests.Values)
        {
            tcs.TrySetCanceled();
        }
        _pendingRequests.Clear();
    }
}

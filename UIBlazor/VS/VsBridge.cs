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

    /// <summary>
    /// Сколько ждать ответа хоста на <paramref name="action"/> при настройке <paramref name="settingMs"/>.
    /// Вынесено в метод, чтобы инвариант «клиент ждёт СТРОГО дольше всей хостовой цепочки» был
    /// проверим тестом без ожиданий.
    /// <para>
    /// Хост убивает команду на лимите из настройки и отвечает честным
    /// «Command timed out after N ms». Если клиент ждёт ТО ЖЕ время, он регулярно выигрывает гонку
    /// и подменяет этот ответ своей аварией (<c>Transport failure: Request timed out</c>), а
    /// убитая команда уже никто не прочитает.
    /// </para>
    /// <para>
    /// У MCP цепочка длиннее на <see cref="ToolTimeout.McpHostGraceMs"/>: вызов идёт через отдельный
    /// процесс McpHost, и хост ждёт его дольше самого вызова. Поэтому клиенту нужен запас не только
    /// на обратный путь, но и на это звено — иначе гонка просто переезжает в MCP-канал. Дефолт тоже
    /// берётся по каналу: хост без поля применит DefaultMcpCallMs, и клиент с bash-дефолтом снова
    /// ждал бы меньше хоста.
    /// </para>
    /// </summary>
    internal static TimeSpan ComputeWait(string action, int settingMs)
        => action == BasicEnum.McpCallTool
            ? ToolTimeout.ClientWait(settingMs, ToolTimeout.McpHostGraceMs, ToolTimeout.DefaultMcpCallMs)
            : ToolTimeout.ClientWait(settingMs);

    private async Task<VsResponse> SendRequestAsync(VsRequest request, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync();

        var tcs = new TaskCompletionSource<VsResponse>();
        _pendingRequests.TryAdd(request.CorrelationId, tcs);

        try
        {
            // Снимок настройки ДО отправки: лимит на хосте зафиксирован в payload уже на моменте
            // вызова тула, поэтому чтение настройки ПОСЛЕ postVsMessage могло взять более новое
            // (меньшее) значение и снова сделать клиента короче хоста.
            var timeOut = ComputeWait(request.Action, _commonOptions.Current.ToolTimeoutMs);
#if DEBUG
            // Дебаг не быстрый, но не бесконечный: сутки тишины выглядели в UI ровно как
            // «приложение повисло» — отличить «тулза думает» от «мост мёртв» невозможно.
            // 30 минут всё ещё хватает на отладку VS с точкой останова.
            timeOut = TimeSpan.FromMinutes(30);
#endif

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

            // Both registrations are disposed with the wait. An undisposed registration keeps the
            // TCS (and the request payload plus its continuation) alive until the token is
            // collected, and it fires on a token that may be cancelled long after this request was
            // already answered.
            using var cancellationRegistration =
                cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

            using var timeoutCts = new CancellationTokenSource(timeOut);
            using var timeoutRegistration = timeoutCts.Token.Register(() =>
                tcs.TrySetException(new TimeoutException("Request timed out")));

            // Ждем ответа
            return await tcs.Task;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The user cancelled the run. This is NOT a transport failure.
            //
            // Swallowing it into `Success = false` handed the model the tool result
            // "Transport failure: The operation was canceled." — a lie: the tool did not fail, it
            // was never finished. It also wrote an ERROR log entry with a stack trace for a
            // completely expected event, and it kept the run unwinding its tool batch one call at
            // a time instead of stopping at the next check.
            //
            // Both run loops already own cancellation as a terminal state
            // (AiChat.ExecuteToolBatchAsync and AfSubAgentOrchestrator.ExecuteBranchToolBatchAsync
            // catch OCE on the run token, then stamp a result on every remaining call), so the
            // exception is propagated, not translated.
            throw;
        }
        catch (Exception ex)
        {
            // Здесь остаются только настоящие аварии транспорта (таймаут, сбой JS). Отмена
            // сюда больше не доходит — она обрабатывается отдельной веткой выше.
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

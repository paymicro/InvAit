using System.Collections.Concurrent;
using Microsoft.JSInterop;
using UIBlazor.Services;
using UIBlazor.Services.Settings;

namespace UIBlazor.VS;

public class VsBridge : IVsBridge, IDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private readonly ICommonSettingsProvider _commonOptions;
    private readonly IVsCodeContextService _vsCodeContextService;
    private DotNetObjectReference<VsBridge> _dotNetRef;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<VsResponse>> _pendingRequests;
    private bool _isInitialized;

    public VsBridge(IJSRuntime jsRuntime,
         ICommonSettingsProvider commonSettingsProvider,
         IVsCodeContextService vsCodeContextService)
    {
        _jsRuntime = jsRuntime;
        _commonOptions = commonSettingsProvider;
        _vsCodeContextService = vsCodeContextService;
        _pendingRequests = new ConcurrentDictionary<string, TaskCompletionSource<VsResponse>>();
    }

    public async Task InitializeAsync()
    {
        if (!_isInitialized)
        {
            _dotNetRef = DotNetObjectReference.Create(this);
            var result = await _jsRuntime.InvokeAsync<string>("setVsBridgeHandler", _dotNetRef);
            _isInitialized = result == "OK";

            if (_isInitialized)
            {
                // Notify Host that we are ready to receive messages (e.g. initial context)
                await _jsRuntime.InvokeVoidAsync("postVsMessage", new VsRequest { Action = BasicEnum.UIReady });
            }
        }
    }

    public async Task<VsToolResult> ExecuteToolAsync(string name, IReadOnlyDictionary<string, object>? args = null, CancellationToken cancellationToken = default)
    {
        var request = new VsRequest
        {
            Action = name,
            Payload = args != null ? JsonUtils.Serialize(args) : null
        };

        var response = await SendRequestAsync(request, cancellationToken);

        return Convert(request, response);
    }

    private VsToolResult Convert(VsRequest vsRequest, VsResponse vsResponse)
    {
        return new VsToolResult
        {
            Args = vsRequest.Payload ?? string.Empty,
            ErrorMessage = vsResponse.Error ?? string.Empty,
            Name = vsRequest.Action,
            Result = vsResponse.Payload ?? vsResponse.Error ?? string.Empty,
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
                    Error = $"WebView2 API is`t find."
                };
            }

            var timeOut = TimeSpan.FromMilliseconds(_commonOptions.Current.ToolTimeoutMs);
#if DEBUG
            // Дебаг не быстрый)
            timeOut = TimeSpan.FromDays(1);
#endif
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeOut);
            timeoutCts.Token.Register(() =>
                tcs.TrySetException(new TimeoutException("Request timed out")));

            // Ждем ответа
            return await tcs.Task;
        }
        catch (Exception ex)
        {
            return new VsResponse
            {
                Success = false,
                Error = $"Error getting response: {ex.Message}"
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
                        var context = JsonUtils.Deserialize<VsCodeContext>(message.Payload);
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
                if (response.Success)
                {
                    tcs.SetResult(response);
                }
                else
                {
                    tcs.SetException(new Exception(response.Error ?? "Request failed"));
                }
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
        if (!_isInitialized)
        {
            await InitializeAsync();
        }
    }

    public void Dispose()
    {
        _dotNetRef?.Dispose();

        // Отменяем все ожидающие запросы
        foreach (var tcs in _pendingRequests.Values)
        {
            tcs.TrySetCanceled();
        }
        _pendingRequests.Clear();
    }
}

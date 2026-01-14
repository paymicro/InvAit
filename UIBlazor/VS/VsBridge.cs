using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.JSInterop;
using Radzen;
using Shared.Contracts;
using UIBlazor.Agents;
using UIBlazor.Utils;

namespace UIBlazor.VS;

public class VsBridge : IVsBridge, IDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private readonly NotificationService _notificationService;
    private DotNetObjectReference<VsBridge> _dotNetRef;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<VsResponse>> _pendingRequests;
    private bool _isInitialized;

    public VsBridge(IJSRuntime jsRuntime, NotificationService notificationService)
    {
        _jsRuntime = jsRuntime;
        _notificationService = notificationService;
        _pendingRequests = new ConcurrentDictionary<string, TaskCompletionSource<VsResponse>>();
    }

    public async Task InitializeAsync()
    {
        if (!_isInitialized)
        {
            _dotNetRef = DotNetObjectReference.Create(this);
            await _jsRuntime.InvokeVoidAsync("setVsBridgeHandler", _dotNetRef);
            _isInitialized = true;
        }
    }

    public async Task<string> ReadOpenFileAsync()
    {
        var request = new VsRequest { Action = BuiltInToolEnum.ReadOpenFile };

        var response = await SendRequestAsync(request);
        if (response == null)
        {
            return string.Empty;
        }
        else if (response.Success)
        {
            return response.Payload ?? string.Empty;
        }
        else
        {
            return response.Error ?? string.Empty;
        }
    }


    public async Task<VsToolResult> ExecuteToolAsync(string name, IReadOnlyDictionary<string, object>? args = null)
    {
        var request = new VsRequest
        {
            Action = name,
            Payload = args != null ? JsonUtils.Serialize(args) : null
        };

        var response = await SendRequestAsync(request);
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

    private async Task<VsResponse> SendRequestAsync(VsRequest request)
    {
        await EnsureInitializedAsync();

        var tcs = new TaskCompletionSource<VsResponse>();
        _pendingRequests.TryAdd(request.CorrelationId, tcs);

        try
        {
            // Отправляем запрос
            await _jsRuntime.InvokeVoidAsync("postVsMessage", request);

            // Устанавливаем таймаут
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            timeoutCts.Token.Register(() =>
                tcs.TrySetException(new TimeoutException("Request timed out")));

            // Ждем ответа
            return await tcs.Task;
        }
        catch (Exception ex)
        {
            _notificationService.Notify(new NotificationMessage {
                Severity = NotificationSeverity.Error,
                Summary = $"Error getting response '{request.Action}'",
                Detail = ex.Message,
                Duration = 6_000,
                ShowProgress = true
            });
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

    [JSInvokable("HandleVsResponse")]
    public async Task HandleVsResponseAsync(string responseJson, bool isMessage)
    {
        try
        {
            if (isMessage)
            {
                var message = JsonSerializer.Deserialize<VsMessage>(responseJson, JsonSerializerOptions.Web);
                if (message == null || string.IsNullOrEmpty(message.CorrelationId))
                {
                    Console.WriteLine("Invalid message received");
                    return;
                }

                //TODO: обработка сообщений (выделение, выбран документ ...)
            }

            var response = JsonSerializer.Deserialize<VsResponse>(responseJson, JsonSerializerOptions.Web);

            if (response == null || string.IsNullOrEmpty(response.CorrelationId))
            {
                Console.WriteLine("Invalid response received");
                return;
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
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.JSInterop;
using Radzen;
using Shared.Contracts;

namespace UIBlazor.VS;

public class VsBridgeProxy : IVsBridge, IDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private readonly NotificationService _notificationService;
    private DotNetObjectReference<VsBridgeProxy> _dotNetRef;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<VsResponse>> _pendingRequests;
    private bool _isInitialized;

    public VsBridgeProxy(IJSRuntime jsRuntime, NotificationService notificationService)
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

    public async Task<string> GetActiveDocumentContentAsync()
    {
        var request = new VsRequest { Action = "getActiveDocumentContent" };

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

    private async Task<VsResponse?> SendRequestAsync(VsRequest request)
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
            _notificationService.Notify(NotificationSeverity.Error, $"Error getting response '{request.Action}'", ex.Message);
            return null;
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

    public Task<List<string>> GetOpenDocumentsAsync()
    {
        return Task.FromResult(new List<string>());
    }

    public Task<string> GetSelectedTextAsync()
    {
        return Task.FromResult("");
    }

    public Task InsertTextAtPositionAsync(string filePath, int line, int column, string text)
    {
        return Task.FromResult("");
    }
}
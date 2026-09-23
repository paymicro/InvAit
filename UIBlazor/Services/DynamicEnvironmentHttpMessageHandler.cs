using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Microsoft.JSInterop;

namespace UIBlazor.Services;

public class DynamicEnvironmentHttpMessageHandler(IJSRuntime jsRuntime) : DelegatingHandler
{
    private bool? _isVsCode;

    // Храним активные потоки ответов по их Request ID
    public static readonly ConcurrentDictionary<string, ChunkedStream> ActiveStreams = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Кэшируем результат проверки окружения, чтобы не дергать JS при каждом запросе
        if (!_isVsCode.HasValue)
        {
            try
            {
                // Используем eval вместо вызова функции — не зависит от загрузки app.js
                _isVsCode = await jsRuntime.InvokeAsync<bool>("eval", "window.parent !== window");
            }
            catch (Exception ex)
            {
                await jsRuntime.InvokeVoidAsync("console.log", $"[InvAit C#] isVsCode eval THREW: {ex.Message}");
                _isVsCode = false;
            }
        }

        var method = request.Method.Method;
        var url = request.RequestUri?.ToString() ?? "?";

        // Мы НЕ в VS Code (например, обычный WebView2, Chrome, Safari)
        if (!_isVsCode.Value)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        return await SendViaVsCodeProxyAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendViaVsCodeProxyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString();

        string? requestBody = null;
        if (request.Content != null)
        {
            requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        var headers = new Dictionary<string, string>();
        foreach (var header in request.Headers)
            headers[header.Key] = string.Join(", ", header.Value);

        if (request.Content?.Headers != null)
        {
            foreach (var header in request.Content.Headers)
                headers[header.Key] = string.Join(", ", header.Value);
        }

        var tcs = new TaskCompletionSource<HttpResponseMessage>();
        var responseBridge = new VscodeResponseBridge(requestId, tcs, jsRuntime);
        var dotNetRef = DotNetObjectReference.Create(responseBridge);
        responseBridge.DotNetRef = dotNetRef;

        // Dispose on cancellation — covers the case where the request is cancelled
        // before headers or body arrive.
        var cancellationRegistration = cancellationToken.Register(() =>
        {
            tcs.TrySetCanceled(cancellationToken);
            DynamicEnvironmentHttpMessageHandler.ActiveStreams.TryRemove(requestId, out _);
            try { dotNetRef.Dispose(); } catch { /* already disposed */ }
        });

        try
        {
            await jsRuntime.InvokeVoidAsync("vscodeInterop.sendNetworkRequest",
                requestId,
                request.RequestUri?.ToString(),
                request.Method.Method,
                headers,
                requestBody,
                dotNetRef
            );
        }
        catch (Exception ex)
        {
            cancellationRegistration.Dispose();
            try { dotNetRef.Dispose(); } catch { }
            await jsRuntime.InvokeVoidAsync("console.log", $"[InvAit C#] sendNetworkRequest JS call THREW: {ex.Message}");
            throw;
        }

        try
        {
            // tcs.Task completes when ReceiveHeaders is called (headers received).
            // The response body is streamed AFTER this via ReceiveChunk/ReceiveEnd,
            // so dotNetRef must stay alive until ReceiveEnd disposes it.
            return await tcs.Task;
        }
        catch (Exception ex)
        {
            // Only clean up on error/cancellation — on success, dotNetRef is
            // disposed by VscodeResponseBridge.ReceiveEnd after the stream completes.
            cancellationRegistration.Dispose();
            DynamicEnvironmentHttpMessageHandler.ActiveStreams.TryRemove(requestId, out _);
            try { dotNetRef.Dispose(); } catch { }
            await jsRuntime.InvokeVoidAsync("console.log", $"[InvAit C#] tcs.Task threw: {ex.Message}");
            throw;
        }
    }
}

// Поток, в который JS будет асинхронно записывать чанки, а HttpClient Блейзора — читать их
public class ChunkedStream : Stream
{
    private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
    private byte[]? _remaining;
    private int _remainingOffset;

    public void PushChunk(byte[] chunk) => _channel.Writer.TryWrite(chunk);
    public void Complete() => _channel.Writer.TryComplete();
    public void Error(Exception ex) => _channel.Writer.TryComplete(ex);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // If we have remaining bytes from a partially-consumed chunk, serve them first
        if (_remaining != null)
        {
            var bytesToCopy = Math.Min(buffer.Length, _remaining.Length - _remainingOffset);
            _remaining.AsMemory(_remainingOffset, bytesToCopy).CopyTo(buffer);
            _remainingOffset += bytesToCopy;
            if (_remainingOffset >= _remaining.Length)
                _remaining = null;
            return bytesToCopy;
        }

        if (!await _channel.Reader.WaitToReadAsync(cancellationToken))
            return 0;

        if (!_channel.Reader.TryRead(out var chunk))
            return 0;

        var copyCount = Math.Min(buffer.Length, chunk.Length);
        chunk.AsMemory(0, copyCount).CopyTo(buffer);

        // If chunk is larger than buffer, save the remainder for next read
        if (copyCount < chunk.Length)
        {
            _remaining = chunk;
            _remainingOffset = copyCount;
        }

        return copyCount;
    }

    public override int Read(byte[] buffer, int offset, int count) => 0;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

// Мост для обработки событий ответа и чанков
public class VscodeResponseBridge
{
    private readonly string _requestId;
    private readonly TaskCompletionSource<HttpResponseMessage> _tcs;
    private readonly IJSRuntime _jsRuntime;
    private ChunkedStream? _stream;

    /// <summary>
    /// Set by the handler after creation so ReceiveEnd can dispose it
    /// once the response stream is fully delivered.
    /// </summary>
    public DotNetObjectReference<VscodeResponseBridge>? DotNetRef { get; set; }

    public VscodeResponseBridge(string requestId, TaskCompletionSource<HttpResponseMessage> tcs, IJSRuntime jsRuntime)
    {
        _requestId = requestId;
        _tcs = tcs;
        _jsRuntime = jsRuntime;
    }

    [JSInvokable]
    public void ReceiveHeaders(int statusCode, string statusText)
    {
        _stream = new ChunkedStream();
        DynamicEnvironmentHttpMessageHandler.ActiveStreams[_requestId] = _stream;

        var response = new HttpResponseMessage((HttpStatusCode)statusCode)
        {
            // Отдаем поток в контент — HttpClient начнет его читать по мере поступления данных
            Content = new StreamContent(_stream)
        };

        _tcs.SetResult(response);
    }

    [JSInvokable]
    public void ReceiveChunk(string chunkText)
    {
        if (_stream == null) return;
        var bytes = Encoding.UTF8.GetBytes(chunkText);
        _stream.PushChunk(bytes);
    }

    [JSInvokable]
    public void ReceiveEnd(bool success, string? error)
    {
        if (!success && error != null)
        {
            // Handle the case where ReceiveHeaders was never called
            _tcs.TrySetException(new Exception(error));

            if (DynamicEnvironmentHttpMessageHandler.ActiveStreams.TryRemove(_requestId, out var stream))
                stream.Error(new Exception(error));
        }
        else
        {
            if (DynamicEnvironmentHttpMessageHandler.ActiveStreams.TryRemove(_requestId, out var stream))
            {
                stream.Complete();
            }
            else
            {
                // Stream ended successfully but ReceiveHeaders was never called
                _tcs.TrySetException(new InvalidOperationException("Stream ended before headers received"));
            }
        }

        // Clean up the DotNetObjectReference now that the stream is fully delivered.
        // This is the normal disposal path — the JS side no longer needs to call us.
        try { DotNetRef?.Dispose(); } catch { /* already disposed via cancellation */ }
    }
}

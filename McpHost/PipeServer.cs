using System.IO.Pipes;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Threading.Channels;
using Shared.Contracts.McpHost;
using Shared.Ipc;

namespace McpHost;

public sealed class PipeServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _pipeName;
    private readonly RequestDispatcher _dispatcher;
    private readonly int _idleExitMs;
    private long _lastActivityTicks;

    public PipeServer(string pipeName, RequestDispatcher dispatcher, int idleExitMs)
    {
        _pipeName = pipeName;
        _dispatcher = dispatcher;
        _idleExitMs = idleExitMs;
        Touch();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var idleCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var idleTimer = new System.Threading.Timer(
            _ => CheckIdle(idleCts, linked),
            null,
            TimeSpan.FromMilliseconds(Math.Min(1000, _idleExitMs)),
            TimeSpan.FromMilliseconds(500));

        while (!linked.IsCancellationRequested && !_dispatcher.StopRequested)
        {
            await using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await server.WaitForConnectionAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            Touch();
            Console.WriteLine("[mcphost] client connected.");
            await ServeConnectionAsync(server, linked.Token);
            Console.WriteLine("[mcphost] client disconnected.");

            if (_dispatcher.StopRequested || linked.IsCancellationRequested)
                break;
        }

        idleTimer.Dispose();

        if (!_dispatcher.StopRequested && !cancellationToken.IsCancellationRequested && idleCts.IsCancellationRequested)
            Console.WriteLine("[mcphost] idle timeout reached, exiting.");
    }

    private void CheckIdle(CancellationTokenSource idleCts, CancellationTokenSource linked)
    {
        var elapsedMs = (Environment.TickCount64 - Interlocked.Read(ref _lastActivityTicks));
        if (elapsedMs >= _idleExitMs)
        {
            Console.WriteLine($"[mcphost] no activity for {elapsedMs} ms, shutting down.");
            try
            {
                idleCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                linked.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, Environment.TickCount64);

    private async Task ServeConnectionAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });

        var writerTask = WriteLoopAsync(stream, channel.Reader);
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_dispatcher.StopRequested)
            {
                Touch();

                var readTask = FrameCodec.ReadFrameAsync(stream, McpHostProtocol.MaxFrameBytes, cancellationToken);
                var completed = await Task.WhenAny(readTask, _dispatcher.WaitStoppedAsync());
                if (completed != readTask)
                {
                    _ = readTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    break;
                }

                byte[]? payload;
                try
                {
                    payload = await readTask;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    break;
                }

                if (payload == null)
                    break;

                McpHostRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<McpHostRequest>(payload, JsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (request == null)
                    continue;

                _ = Task.Run(() => DispatchAsync(request, channel.Writer, cancellationToken), CancellationToken.None);
            }
        }
        finally
        {
            channel.Writer.TryComplete();
            await writerTask;
        }
    }

    private async Task DispatchAsync(McpHostRequest request, ChannelWriter<byte[]> writer, CancellationToken cancellationToken)
    {
        var succeeded = false;
        try
        {
            var response = await _dispatcher.HandleAsync(request);
            succeeded = response.Success;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
            await writer.WriteAsync(bytes, cancellationToken);
            Touch();
        }
        catch (Exception ex) when (ex is ChannelClosedException or OperationCanceledException)
        {
        }

        if (request.Method == McpHostMethods.Shutdown && succeeded && !_dispatcher.StopRequested)
        {
            Console.WriteLine("[mcphost] shutdown requested.");
            _dispatcher.MarkStopRequested();
        }
    }

    private static async Task WriteLoopAsync(NamedPipeServerStream stream, ChannelReader<byte[]> reader)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync())
            {
                await FrameCodec.WriteFrameAsync(stream, frame);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }
}

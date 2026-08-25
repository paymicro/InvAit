using System.IO;
using System.Text;
using Shared.Contracts.McpHost;
using Shared.Ipc;

namespace ToolCore.Tests;

public class FrameCodecTests
{
    [Fact]
    public async Task WriteThenRead_RoundTripsPayload()
    {
        using var stream = new MemoryStream();
        var payload = Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"ping\"}");

        await FrameCodec.WriteFrameAsync(stream, payload);

        stream.Position = 0;
        var result = await FrameCodec.ReadFrameAsync(stream, McpHostProtocol.MaxFrameBytes);

        Assert.NotNull(result);
        Assert.Equal(payload, result);
    }

    [Fact]
    public async Task WriteMultipleFrames_ReadInOrder()
    {
        using var stream = new MemoryStream();
        var first = Encoding.UTF8.GetBytes("first");
        var second = Encoding.UTF8.GetBytes("second-payload");
        var third = Array.Empty<byte>();

        await FrameCodec.WriteFrameAsync(stream, first);
        await FrameCodec.WriteFrameAsync(stream, second);
        await FrameCodec.WriteFrameAsync(stream, third);

        stream.Position = 0;
        Assert.Equal(first, await FrameCodec.ReadFrameAsync(stream, 1024));
        Assert.Equal(second, await FrameCodec.ReadFrameAsync(stream, 1024));
        var empty = await FrameCodec.ReadFrameAsync(stream, 1024);
        Assert.NotNull(empty);
        Assert.Empty(empty);
    }

    [Fact]
    public async Task ReadAtStreamEnd_ReturnsNull()
    {
        using var stream = new MemoryStream(Array.Empty<byte>());
        var result = await FrameCodec.ReadFrameAsync(stream, 1024);
        Assert.Null(result);
    }

    [Fact]
    public async Task ReadMidFrameEof_ThrowsIOException()
    {
        using var stream = new MemoryStream(new byte[] { 10, 0, 0, 0, 1, 2 });
        await Assert.ThrowsAnyAsync<IOException>(() => FrameCodec.ReadFrameAsync(stream, 1024));
    }

    [Fact]
    public async Task ReadInvalidLength_ThrowsIOException()
    {
        var header = new byte[4];
        FrameCodec.WriteInt32LittleEndian(header, 0, -5);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAnyAsync<IOException>(() => FrameCodec.ReadFrameAsync(stream, 1024));
    }

    [Fact]
    public async Task ReadOversizedLength_ThrowsIOException()
    {
        var header = new byte[4];
        FrameCodec.WriteInt32LittleEndian(header, 0, 9999);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAnyAsync<IOException>(() => FrameCodec.ReadFrameAsync(stream, 1024));
    }

    [Fact]
    public async Task WriteOversizedPayload_ThrowsIOException()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAnyAsync<IOException>(
            () => FrameCodec.WriteFrameAsync(stream, new byte[McpHostProtocol.MaxFrameBytes + 1]));
    }
}

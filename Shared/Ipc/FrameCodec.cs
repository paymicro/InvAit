namespace Shared.Ipc;

/// <summary>
/// Length-prefixed frame codec for stream transports (named pipes, stdio).
/// Frame layout: 4-byte little-endian payload length + raw payload bytes.
/// </summary>
public static class FrameCodec
{
    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length > Contracts.McpHost.McpHostProtocol.MaxFrameBytes)
            throw new IOException($"Frame payload exceeds limit of {Contracts.McpHost.McpHostProtocol.MaxFrameBytes} bytes.");

        var packet = new byte[4 + payload.Length];
        WriteInt32LittleEndian(packet, 0, payload.Length);
        payload.Span.CopyTo(packet.AsSpan(4));

#if NETSTANDARD2_0
        await stream.WriteAsync(packet, 0, packet.Length, cancellationToken).ConfigureAwait(false);
#else
        await stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
#endif
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <returns>Payload bytes, or null when the stream ends cleanly before any byte of a new frame.</returns>
    /// <exception cref="IOException">Stream ends mid-frame or the declared length exceeds <paramref name="maxFrameBytes"/>.</exception>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, int maxFrameBytes, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false))
            return null;

        var length = ReadInt32LittleEndian(header);
        if (length < 0 || length > maxFrameBytes)
            throw new IOException($"Invalid frame length {length} (max {maxFrameBytes}). Stream is likely desynchronized.");

        if (length == 0) return [];

        var payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false))
            throw new IOException("Stream ended mid-frame.");

        return payload;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
#if NETSTANDARD2_0
            var read = await stream.ReadAsync(buffer, total, buffer.Length - total, cancellationToken).ConfigureAwait(false);
#else
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
#endif
            if (read <= 0) return false;
            total += read;
        }
        return true;
    }

    public static void WriteInt32LittleEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    public static int ReadInt32LittleEndian(byte[] buffer)
        => buffer[0] | buffer[1] << 8 | buffer[2] << 16 | buffer[3] << 24;
}

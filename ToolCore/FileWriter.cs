using System.IO.Abstractions;
using System.Text;
using Shared.Contracts;

namespace ToolCore;

/// <summary>How `create_file` treats an existing file.</summary>
public enum FileWriteMode
{
    /// <summary>Replace the whole file (default).</summary>
    Overwrite,

    /// <summary>Add the content to the end of the file.</summary>
    Append,

    /// <summary>Add the content to the start of the file.</summary>
    Prepend
}

/// <summary>Outcome of a `create_file` write; <see cref="Payload"/> is what the model reads.</summary>
public sealed class FileWriteResult
{
    private FileWriteResult(bool success, string payload)
    {
        Success = success;
        Payload = payload;
    }

    public static FileWriteResult Ok(string payload) => new(true, payload);

    /// <summary>A refusal: the SAME string is the payload, because <c>VsResponse</c> is bimodal
    /// (discriminated by <see cref="Success"/>) and carries no second error channel.</summary>
    public static FileWriteResult Fail(string error) => new(false, error);

    public bool Success { get; }

    /// <summary>Human-readable text for the tool result — always names what was actually written,
    /// or why nothing was.</summary>
    public string Payload { get; }
}

/// <summary>
/// Writes file content with the encoding and the line ending the TREE asks for.
///
/// WHY THIS EXISTS: the old `create_file` was `File.WriteAllText(path, content, Encoding.UTF8)`,
/// which in .NET means **UTF-8 with a BOM**. In a tree that declares `charset = utf-8` that BOM is
/// a defect the model cannot see: git shows a changed first line, and C# treats the prefix as a
/// stray U+FEFF before the first token.
///
/// RULES (each one is a decision, not an accident):
/// - NEW file: encoding from `.editorconfig` `charset`, default UTF-8 WITHOUT BOM; that is the
///   value from `.editorconfig`, never a hard-coded one.
/// - EXISTING file: its own bytes win, and they are never DECODED in order to be re-encoded. The
///   file is treated as `preamble + body` and only the new chunk is encoded; the body is spliced.
///   Decoding a cp1251/latin1 file as UTF-8 turns every high byte into U+FFFD and writes those back
///   as EF BF BD — the file is destroyed while the report claims success.
/// - A file WITHOUT a BOM is PROBED, not assumed: valid strict UTF-8 means `utf-8`, anything else
///   needs an explicit `.editorconfig` charset, otherwise the write is REFUSED and nothing is
///   written. Saying no loses nothing; guessing loses the file.
/// - A separator/encoding is only ENFORCED when it is known: a new file with no `.editorconfig`
///   value keeps the content verbatim, and the separator of an existing file is sniffed from its
///   own BYTES — never from the platform newline, which would make the result host-dependent and
///   diverge from the VS Code host. The sniffed value is the MAJORITY of CRLF/LF/CR (ties CRLF >
///   LF > CR, `\n` when the file has no break at all) and that algorithm lives in ONE place:
///   <see cref="TextFileCodec.DetectSeparator"/>.
/// - Only the text this call writes is normalised; untouched bytes of an existing file are not
///   rewritten. If the resolved `.editorconfig` disagrees with what the file actually has, that is
///   REPORTED in <see cref="FileWriteResult.Payload"/> rather than silently fixed.
/// </summary>
public sealed class FileWriter
{
    /// <summary>
    /// How much of an existing file is sniffed for `overwrite`: the preamble and the first line
    /// break are always inside it, and it keeps a 3 MB file from being read just to be replaced.
    /// </summary>
    private const int _sniffBytes = 64 * 1024;

    private readonly IFileSystem _fs;
    private readonly EditorConfigResolver _editorConfig;

    public FileWriter(IFileSystem? fs = null)
    {
        _fs = fs ?? new FileSystem();
        _editorConfig = new EditorConfigResolver(_fs);
    }

    /// <summary>
    /// Parses the tool's `mode` argument; null/empty means overwrite.
    /// <para>
    /// The alias table itself lives in <see cref="FileWriteModeNames"/> (<c>Shared</c>), because the
    /// UI has to resolve the SAME aliases to label the call correctly: `create` and `replace` are
    /// both `overwrite`, and a badge built on a second copy of this table would eventually claim a
    /// mode the host never applied.
    /// </para>
    /// </summary>
    public static FileWriteMode ParseMode(string? mode)
    {
        if (!FileWriteModeNames.TryNormalize(mode, out var normalized))
        {
            throw new ArgumentException($"Unknown mode '{mode}'. Use overwrite, append or prepend.");
        }

        return normalized switch
        {
            FileWriteModeNames.Append => FileWriteMode.Append,
            FileWriteModeNames.Prepend => FileWriteMode.Prepend,
            _ => FileWriteMode.Overwrite
        };
    }

    /// <summary>String overload used by the hosts: an unknown mode becomes a per-call failure, not an exception.</summary>
    public Task<FileWriteResult> WriteAsync(string path, string content, string? mode, CancellationToken cancellationToken = default)
    {
        FileWriteMode parsed;
        try
        {
            parsed = ParseMode(mode);
        }
        catch (ArgumentException e)
        {
            return Task.FromResult(FileWriteResult.Fail(e.Message));
        }

        return WriteAsync(path, content, parsed, cancellationToken);
    }

    public Task<FileWriteResult> WriteAsync(string path, string content, FileWriteMode mode, CancellationToken cancellationToken = default)
    {
        // The VS host AWAITS this on the UI thread: doing the I/O inline froze the editor for the
        // whole call (a 3 MB file on a network share is seconds). The work therefore runs on the
        // pool, and the token is re-checked inside WriteCore right before the write, so a cancelled
        // call cannot leave a half-written file.
        return Task.Run(() => WriteCore(path, content, mode, cancellationToken), cancellationToken);
    }

    private FileWriteResult WriteCore(string path, string content, FileWriteMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = _fs.Path.GetFullPath(path);
        var settings = _editorConfig.Resolve(fullPath);
        var exists = _fs.File.Exists(fullPath);

        var directory = _fs.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !_fs.Directory.Exists(directory))
            _fs.Directory.CreateDirectory(directory!);

        return exists
            ? WriteExisting(path, fullPath, content, mode, settings, cancellationToken)
            : WriteNew(path, fullPath, content, settings, cancellationToken);
    }

    /// <summary>A file that does not exist yet: the tree decides the bytes, the caller decides the text.</summary>
    private FileWriteResult WriteNew(string path, string fullPath, string content, EditorConfigSettings settings, CancellationToken cancellationToken)
    {
        var configuredEncoding = settings.CreateEncoding();
        var fallbackEncoding = configuredEncoding ?? new UTF8Encoding(false);
        var charsetName = configuredEncoding != null ? settings.Charset! : "utf-8";
        // A new file's prefix is the one its charset DECLARES (`.editorconfig` is the only source),
        // and the bytes are taken from the identity instead of a second `Encoding.GetPreamble`.
        var identity = FileIdentity.FromCharset(fallbackEncoding, fallbackEncoding, charsetName, fallbackEncoding.GetPreamble());
        var encoding = identity.Encoding;

        // A NEW file is the only place where `.editorconfig` may shape the CONTENT: its
        // `end_of_line` is applied, and without one the text goes in verbatim — turning `\n` into
        // the platform newline is a change the caller never asked for.
        var separator = settings.Separator ?? "\n";
        var separatorEnforced = settings.Separator != null;
        var text = settings.Separator == null ? content : TextFileCodec.NormalizeEol(content, settings.Separator);

        var notes = new StringBuilder();

        // A U+FEFF arriving inside `content` is never the BOM the writer should emit — the prefix
        // comes from `charset` and nothing else. Two cases, both wrong on disk:
        //   `charset = utf-8`       → the character sits where the encoding forbids a BOM;
        //   `charset = utf-8-bom`   → the preamble is emitted AND the content carries one, so the
        //                            file starts with two, which is a compile error before the
        //                            first C# token. The guard used to cover only the first case.
        // `while`, not `if`: `"\uFEFF\uFEFF"` would otherwise leave one at the head of the body.
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            var stripped = 0;
            while (text.Length > 0 && text[0] == '\uFEFF')
            {
                text = text.Substring(1);
                stripped++;
            }

            notes.Append(stripped == 1
                ? (identity.Preamble.Length == 0
                    ? $" Note: a leading U+FEFF was removed because charset '{charsetName}' means no BOM."
                    : " Note: a leading U+FEFF was removed from the content: the file's BOM comes from its encoding and must not be repeated.")
                : $" Note: {stripped} leading U+FEFF characters were removed from the content: the file's BOM comes from its encoding and must not be repeated.");
        }

        byte[] body;
        try
        {
            // Throwing fallback, not the default replacement: `charset = latin1` must not turn
            // `привет` into `??????` and call that a success.
            body = encoding.GetBytes(text);
        }
        catch (EncoderFallbackException e)
        {
            return FileWriteResult.Fail(CannotRepresent(charsetName, e));
        }

        cancellationToken.ThrowIfCancellationRequested();
        _fs.File.WriteAllBytes(fullPath, Concat(identity.Preamble, body));

        var report = new StringBuilder();
        report.Append($"File {path} created successfully. Encoding: {charsetName}.");
        if (separatorEnforced)
            report.Append($" Line endings: {TextFileCodec.SeparatorName(separator)}.");

        // An unknown charset is NOT replaced by a silent guess: the caller is told what was written.
        if (settings.Charset != null && configuredEncoding == null)
            report.Append($" Note: charset '{settings.Charset}' is not supported, wrote '{charsetName}' instead.");

        report.Append(notes);
        return FileWriteResult.Ok(report.ToString());
    }

    /// <summary>An existing file: its own bytes decide the encoding AND the separator.</summary>
    private FileWriteResult WriteExisting(string path, string fullPath, string content, FileWriteMode mode, EditorConfigSettings settings, CancellationToken cancellationToken)
    {
        // The file's BOM belongs to the WRITER: it is the one the resolved encoding declares. A
        // U+FEFF arriving inside `content` would therefore be a SECOND one — at the very start of
        // an overwritten body, or buried mid-file by append/prepend. Nothing downstream recognises
        // either, and in C# a stray U+FEFF before the first token is a compile error.
        //
        // The strip comes BEFORE the empty-content guard: a body that was nothing but a U+FEFF is an
        // empty body, and the guard has to see it that way — otherwise the append of "" below would
        // still write the glue and grow the file.
        var notes = new StringBuilder();
        // `while`, not `if`: the note speaks of ONE character, but a model that pastes "\uFEFF\uFEFF"
        // would leave the second one at the head of the body — which is the "two BOMs in a row" this
        // whole branch exists to prevent. Every leading one is removed.
        if (content.Length > 0 && content[0] == '\uFEFF')
        {
            var stripped = 0;
            while (content.Length > 0 && content[0] == '\uFEFF')
            {
                content = content.Substring(1);
                stripped++;
            }

            notes.Append(stripped == 1
                ? " Note: a leading U+FEFF was removed from the content: the file's BOM comes from its encoding and must not be repeated."
                : $" Note: {stripped} leading U+FEFF characters were removed from the content: the file's BOM comes from its encoding and must not be repeated.");
        }

        // Appending nothing must not touch the file at all — not even to read it.
        if (mode != FileWriteMode.Overwrite && content.Length == 0)
            return FileWriteResult.Ok($"Nothing to append to {path}: the content was empty and the file was not modified.");

        // `Overwrite` replaces the body, so only the preamble and the first line break are needed.
        // `Append`/`Prepend` splice into the whole file, so they do read it.
        var all = mode == FileWriteMode.Overwrite ? null : _fs.File.ReadAllBytes(fullPath);
        var sniff = all ?? ReadHead(fullPath, _sniffBytes);

        var identity = FileIdentity.FromBytes(sniff);
        var charsetFromEditorConfig = false;

        // No BOM on disk: PROBE the bytes instead of declaring them UTF-8. Declaring a cp1251 or
        // latin1 file "utf-8" is exactly how its high bytes became U+FFFD and EF BF BD.
        if (!identity.HasBom && !TextFileCodec.IsStrictUtf8(sniff, all != null || sniff.Length < _sniffBytes))
        {
            var configured = settings.CreateEncoding();
            if (configured == null)
            {
                // The refusal must be actionable: name what is missing (or unusable) so the caller
                // can fix the tree instead of retrying the same call.
                var reason = settings.Charset == null
                    ? ".editorconfig declares no charset"
                    : $".editorconfig declares charset '{settings.Charset}', which is not supported";
                return FileWriteResult.Fail(
                    $"File {path} is not valid UTF-8 and has no BOM, and {reason}. " +
                    "Refusing to rewrite it — non-ASCII bytes would be lost. Set charset in .editorconfig, or edit the file with bash.");
            }

            // The tree is the only source that can name this encoding; the file itself has no BOM
            // to say it, and the payload must report where the decision came from.
            identity = FileIdentity.FromCharset(configured, configured, settings.Charset!, identity.Preamble);
            charsetFromEditorConfig = true;
        }

        var encoding = identity.Encoding;
        var charsetName = identity.CharsetName;
        var preamble = identity.Preamble;
        var body = all == null ? Array.Empty<byte>() : Slice(all, preamble.Length, all.Length - preamble.Length);

        var fileSeparator = TextFileCodec.DetectSeparator(sniff, preamble.Length, encoding);
        var separator = fileSeparator ?? settings.Separator ?? "\n";

        byte[] chunk;
        try
        {
            chunk = encoding.GetBytes(TextFileCodec.NormalizeEol(content, separator));
        }
        catch (EncoderFallbackException e)
        {
            return FileWriteResult.Fail(CannotRepresent(charsetName, e));
        }

        var separatorBytes = encoding.GetBytes(separator);

        byte[] bytes;
        switch (mode)
        {
            case FileWriteMode.Append:
                // One separator when the body has no line break of ANY kind at the end, otherwise
                // the old last line and the first new one would merge into a single line. Testing
                // the DOMINANT separator instead put a blank line into a file whose last break was
                // a different kind — and it made this host disagree with the VS Code one.
                bytes = Concat(preamble, body, body.Length > 0 && !TextFileCodec.EndsWithLineBreak(body, encoding) ? separatorBytes : Array.Empty<byte>(), chunk);
                break;

            case FileWriteMode.Prepend:
                // The preamble stays FIRST: `chunk + existing bytes` would bury a BOM in the middle
                // of the file, where nothing recognises it any more. Same "any line break" test as
                // Append, so the two hosts cannot answer differently about the same text.
                bytes = Concat(preamble, chunk, body.Length > 0 && !TextFileCodec.EndsWithLineBreak(chunk, encoding) ? separatorBytes : Array.Empty<byte>(), body);
                break;

            default:
                bytes = Concat(preamble, chunk);
                break;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _fs.File.WriteAllBytes(fullPath, bytes);

        var action = mode switch
        {
            FileWriteMode.Append => "appended to",
            FileWriteMode.Prepend => "prepended to",
            _ => "overwritten"
        };

        var report = new StringBuilder();
        report.Append($"File {path} {action} successfully. Encoding: {charsetName}");
        if (charsetFromEditorConfig)
            report.Append(" (from .editorconfig)");
        report.Append('.');
        report.Append(notes);
        // Printed ONLY when `.editorconfig` enforces `end_of_line`: a separator sniffed from the
        // file is that file's own state, not a rule this write applied (a sniffed value is not
        // "enforced" and reporting it made the same append look like a reformat). One condition,
        // identical to WriteNew's, and the one docs/dev/file-writes.md states.
        // `== separator` as well: the field states the rule THIS write enforced, so a declared
        // `end_of_line` the file does not use must not be printed as if it had been applied — the
        // mismatch note below is what says so, and printing both made the payload contradict itself.
        if (settings.Separator != null && settings.Separator == separator)
            report.Append($" Line endings: {TextFileCodec.SeparatorName(separator)}.");

        // What a caller must be TOLD instead of silently "fixed": the tree asks for one thing and
        // the file has another. Reformatting the tree is a formatter's job, not a writer's.
        if (!charsetFromEditorConfig && settings.Charset != null && settings.Charset != charsetName)
        {
            report.Append($" Note: .editorconfig requires charset '{settings.Charset}' but the file is '{charsetName}'.");
            report.Append(OnlyNewTextUsed(charsetName, mode));
        }

        if (settings.Separator != null && settings.Separator != separator)
        {
            report.Append($" Note: .editorconfig requires end_of_line '{settings.EndOfLine}' " +
                          $"but the file uses '{TextFileCodec.SeparatorName(separator)}'.");
            report.Append(OnlyNewTextUsed(TextFileCodec.SeparatorName(separator), mode));
        }

        return FileWriteResult.Ok(report.ToString());
    }

    /// <summary>
    /// Tail of a mismatch note. Nothing in this class re-encodes untouched bytes, so the only
    /// truthful thing left to say is WHICH part of the file the setting was applied to.
    /// </summary>
    private static string OnlyNewTextUsed(string used, FileWriteMode mode) => mode == FileWriteMode.Overwrite
        ? $" The written content used '{used}'."
        : $" The existing bytes were left untouched; only the new text used '{used}'.";

    private static string CannotRepresent(string charsetName, EncoderFallbackException e) =>
        $"Content cannot be written: charset '{charsetName}' cannot represent one or more characters " +
        $"(first offender: '{Offender(e)}'). The file was NOT modified.";

    /// <summary>The first character the charset could not encode (a surrogate pair reports its high half).</summary>
    private static string Offender(EncoderFallbackException e) =>
        e.IsUnknownSurrogate() ? e.CharUnknownHigh.ToString() : e.CharUnknown.ToString();

    private static byte[] Slice(byte[] source, int offset, int count)
    {
        if (count <= 0)
            return [];

        var slice = new byte[count];
        Buffer.BlockCopy(source, offset, slice, 0, count);
        return slice;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var length = 0;
        foreach (var part in parts)
            length += part.Length;

        var result = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }

        return result;
    }

    /// <summary>First <paramref name="maxBytes"/> bytes of the file — never the whole body of a big one.</summary>
    private byte[] ReadHead(string fullPath, int maxBytes)
    {
        using var stream = _fs.File.OpenRead(fullPath);
        var buffer = new byte[maxBytes];
        var read = 0;
        while (read < maxBytes)
        {
            var chunk = stream.Read(buffer, read, maxBytes - read);
            if (chunk <= 0)
                break;
            read += chunk;
        }

        return read == maxBytes ? buffer : Slice(buffer, 0, read);
    }
}

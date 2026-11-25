using System.Text;

namespace ToolCore;

/// <summary>
/// THE single home for the byte-level rules that identify a text file and take it apart again:
/// the strict-UTF-8 probe, the charset table, the line-break counter and the byte helpers.
///
/// WHY THIS EXISTS: the same questions were answered in three places and the answers disagreed.
/// `FileWriter` and `FileUtils` each sniffed a separator by PRESENCE (CRLF first) while the VS Code
/// reader used MAJORITY, so `a\r\nb\nc\nd\ne` was re-joined with CRLF in the VS host and with LF in
/// VS Code — the same edit produced different bytes on the two hosts. And `edit_file` had no
/// strict-UTF-8 probe at all, so a BOM-less cp1251 file was decoded as UTF-8, its high bytes became
/// U+FFFD and were written back as EF BF BD.
///
/// LINE-BREAK RULE (one algorithm, no exceptions): count CRLF / LF / CR over the file's BYTES —
/// the encoding decides how wide a break is — and take the MOST FREQUENT kind. Ties are broken
/// CRLF &gt; LF &gt; CR. A buffer with no line break has no opinion (null); the callers that need a
/// concrete value use "\n", which is what the VS Code host does too. `Environment.NewLine` is
/// never used: the same file must produce the same bytes on every host.
/// </summary>
internal static class TextFileCodec
{
    /// <summary>
    /// The charset with a THROWING encoder AND decoder: a character the charset cannot represent
    /// must fail the call instead of silently becoming '?', and a byte sequence it cannot decode
    /// must fail instead of becoming U+FFFD. `Clone` is required — an `Encoding` instance handed
    /// out by the runtime is read-only and rejects a fallback assignment.
    /// </summary>
    public static Encoding Strict(Encoding encoding)
    {
        var strict = (Encoding)encoding.Clone();
        strict.EncoderFallback = EncoderFallback.ExceptionFallback;
        strict.DecoderFallback = DecoderFallback.ExceptionFallback;
        return strict;
    }

    /// <summary>
    /// True when the bytes are valid UTF-8 with no replacement character. <paramref name="complete"/>
    /// says whether the buffer is the WHOLE file: a sniff window can end inside a multi-byte
    /// character, and that cut must be held in the decoder state instead of being reported as
    /// invalid — otherwise a valid UTF-8 file would be refused because its 64 KB boundary fell
    /// inside 'ж'.
    /// </summary>
    public static bool IsStrictUtf8(byte[] bytes, bool complete)
    {
        var decoder = new UTF8Encoding(false, throwOnInvalidBytes: true).GetDecoder();
        // A fixed scratch buffer, not one sized to the file: the probe runs on files up to 2 MB and
        // does not need a 4 MB char array to answer a yes/no question.
        var chars = new char[4096];
        var offset = 0;
        try
        {
            while (offset < bytes.Length)
            {
                decoder.Convert(bytes, offset, bytes.Length - offset, chars, 0, chars.Length, false, out var used, out var produced, out _);
                offset += used;
                // No progress at all with a buffer far larger than one character: nothing left to
                // decide here, and an infinite loop is worse than a lenient answer.
                if (used == 0 && produced == 0)
                    break;
            }

            if (complete)
                decoder.Convert(Array.Empty<byte>(), 0, 0, chars, 0, chars.Length, true, out _, out _, out _);

            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// The file's own line break, counted over BYTES from <paramref name="start"/> (past the BOM),
    /// or null when the buffer holds no line break at all. Never <see cref="Environment.NewLine"/>.
    /// </summary>
    public static string? DetectSeparator(byte[] bytes, int start, Encoding encoding) =>
        Count(bytes, start, encoding).Separator;

    /// <summary>More than one KIND of line break (CRLF / lone LF / lone CR) occurs in the bytes.</summary>
    public static bool HasMixedLineEndings(byte[] bytes, int start, Encoding encoding) =>
        Count(bytes, start, encoding).IsMixed;

    /// <summary>
    /// Rewrites every line break kind into <paramref name="separator"/>. Only the text a call WRITES
    /// is normalised — re-encoding untouched bytes is not this method's job.
    /// </summary>
    public static string NormalizeEol(string text, string separator) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", separator);

    /// <summary>Name of a separator for the tool report and for `.editorconfig` comparison.</summary>
    public static string SeparatorName(string separator) => separator switch
    {
        "\r\n" => "crlf",
        "\r" => "cr",
        _ => "lf"
    };

    /// <summary>
    /// THE charset table: the one place a `.editorconfig` `charset` value becomes an `Encoding`.
    /// An unknown value returns null and is never guessed — the caller falls back to its own
    /// default and says so in the tool response.
    /// </summary>
    public static Encoding? CreateEncoding(string? charsetName) => charsetName switch
    {
        "utf-8" => new UTF8Encoding(false),
        "utf-8-bom" => new UTF8Encoding(true),
        "latin1" => Encoding.GetEncoding(28591),
        "utf-16le" => new UnicodeEncoding(false, true),
        "utf-16be" => new UnicodeEncoding(true, true),
        _ => null
    };

    /// <summary>True when <paramref name="bytes"/> begins with <paramref name="prefix"/>.</summary>
    public static bool StartsWith(byte[] bytes, params byte[] prefix)
    {
        if (bytes.Length < prefix.Length)
            return false;

        for (var i = 0; i < prefix.Length; i++)
        {
            if (bytes[i] != prefix[i])
                return false;
        }

        return true;
    }

    /// <summary>True when <paramref name="bytes"/> ends with <paramref name="suffix"/>.</summary>
    public static bool EndsWith(byte[] bytes, byte[] suffix)
    {
        if (suffix.Length == 0 || bytes.Length < suffix.Length)
            return false;

        return MatchesAt(bytes, bytes.Length - suffix.Length, suffix);
    }

    /// <summary>
    /// True when <paramref name="bytes"/> ends with ANY line break (CRLF, LF or a lone CR).
    /// <para>
    /// This — not <see cref="EndsWith"/> against the dominant separator — is the append/prepend glue
    /// rule. A body whose last break is not the dominant kind already HAS a break; adding one more
    /// put a blank line into the file. The two hosts drifted on exactly this and now share it.
    /// </para>
    /// </summary>
    public static bool EndsWithLineBreak(byte[] bytes, Encoding encoding) =>
        EndsWith(bytes, encoding.GetBytes("\r\n")) ||
        EndsWith(bytes, encoding.GetBytes("\n")) ||
        EndsWith(bytes, encoding.GetBytes("\r"));

    /// <summary>
    /// THE separator rule: the most frequent kind wins, ties are broken CRLF &gt; LF &gt; CR, and a
    /// buffer with no line break at all has NO opinion (null) so the caller can apply its own
    /// default — the reader's <c>Separator</c> and the writer's append/prepend glue use "\n".
    /// </summary>
    private static string? Select(int crlf, int lf, int cr)
    {
        if (crlf == 0 && lf == 0 && cr == 0)
            return null;
        if (crlf >= lf && crlf >= cr)
            return "\r\n";
        return lf >= cr ? "\n" : "\r";
    }

    /// <summary>Counts the three kinds of line break over bytes, CRLF being ONE break.</summary>
    private static Counts Count(byte[] bytes, int start, Encoding encoding)
    {
        var width = UnitWidth(encoding);
        var crlfBreak = encoding.GetBytes("\r\n");
        var lfBreak = encoding.GetBytes("\n");
        var crBreak = encoding.GetBytes("\r");

        var crlfCount = 0;
        var lfCount = 0;
        var crCount = 0;

        var i = Math.Max(start, 0);
        while (i + width <= bytes.Length)
        {
            // CRLF first: it CONTAINS a bare CR and a bare LF, so testing them first would count
            // one CRLF break as two.
            if (MatchesAt(bytes, i, crlfBreak))
            {
                crlfCount++;
                i += crlfBreak.Length;
                continue;
            }

            if (MatchesAt(bytes, i, lfBreak))
            {
                lfCount++;
                i += lfBreak.Length;
                continue;
            }

            if (MatchesAt(bytes, i, crBreak))
            {
                crCount++;
                i += crBreak.Length;
                continue;
            }

            i += width;
        }

        return new Counts(crlfCount, lfCount, crCount);
    }

    /// <summary>
    /// How many bytes one character occupies: a line break is 1 byte in UTF-8/latin1 and 2 or 4 in
    /// UTF-16/UTF-32. Scanning on CHARACTER boundaries is what keeps `0A 00` (LF in UTF-16LE) from
    /// being found at an odd offset, straddling two unrelated characters.
    /// </summary>
    private static int UnitWidth(Encoding encoding)
    {
        if (encoding is UTF32Encoding)
            return 4;
        return encoding is UnicodeEncoding ? 2 : 1;
    }

    private static bool MatchesAt(byte[] bytes, int offset, byte[] needle)
    {
        if (offset < 0 || offset + needle.Length > bytes.Length)
            return false;

        for (var i = 0; i < needle.Length; i++)
        {
            if (bytes[offset + i] != needle[i])
                return false;
        }

        return true;
    }

    /// <summary>Line-break counts of one buffer; the rule that turns them into a separator lives in <see cref="Select"/>.</summary>
    private readonly struct Counts
    {
        public Counts(int crlf, int lf, int cr)
        {
            CrLf = crlf;
            Lf = lf;
            Cr = cr;
        }

        public int CrLf { get; }

        public int Lf { get; }

        public int Cr { get; }

        public string? Separator => Select(CrLf, Lf, Cr);

        /// <summary>More than one KIND of break — a homogeneous file is never "mixed".</summary>
        public bool IsMixed => Kinds(CrLf, Lf, Cr) > 1;

        private static int Kinds(int crlf, int lf, int cr) =>
            (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
    }
}

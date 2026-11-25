using System.Text;

namespace ToolCore;

/// <summary>
/// What a text file IS: its body codec, the encoding that must be used to SAVE it back, the
/// charset name `create_file` reports, and the exact BOM bytes found on disk.
///
/// WHY THERE ARE TWO ENCODINGS: `FileUtils.SaveFileAsync` writes through
/// `new StreamWriter(fs, encoding)`, and a StreamWriter emits `encoding.GetPreamble()`. So the
/// encoding handed back to a caller must ROUND-TRIP the preamble (`UTF8Encoding(true)` for
/// `utf-8-bom`, `UnicodeEncoding(bigEndian, byteOrderMark: true)` for UTF-16), while the codec used
/// on the BODY must not add anything of its own. Collapsing the two into one member silently drops
/// the BOM of every `utf-8-bom` / UTF-16 file on the first edit.
///
/// The preamble is carried as BYTES and never re-derived with `Encoding.GetPreamble()` in a caller:
/// a BOM-less file may take its charset from `.editorconfig`, and the file's own prefix must
/// survive unchanged either way.
/// </summary>
internal sealed class FileIdentity
{
    private FileIdentity(Encoding encoding, Encoding savingEncoding, string charsetName, byte[] preamble)
    {
        Encoding = encoding;
        SavingEncoding = savingEncoding;
        CharsetName = charsetName;
        Preamble = preamble;
    }

    /// <summary>
    /// BODY codec: throwing encoder AND decoder, and it contributes no prefix — the caller splices
    /// <see cref="Preamble"/> itself, so decoding/encoding the body cannot duplicate a BOM.
    /// </summary>
    public Encoding Encoding { get; }

    /// <summary>The encoding for a `StreamWriter`-based save: the same charset WITH its preamble.</summary>
    public Encoding SavingEncoding { get; }

    /// <summary>`utf-8` | `utf-8-bom` | `utf-16le` | `utf-16be` | `utf-32le` | `utf-32be` (or the declared `.editorconfig` name).</summary>
    public string CharsetName { get; }

    /// <summary>The BOM bytes this file actually starts with; empty when it has none.</summary>
    public byte[] Preamble { get; }

    public bool HasBom => Preamble.Length > 0;

    /// <summary>
    /// An identity from explicit parts: a charset taken from `.editorconfig` (where the file itself
    /// has no BOM to declare one) or a brand-new file. Both encodings get a THROWING fallback, so a
    /// character the charset cannot represent fails the write instead of becoming '?'.
    /// </summary>
    public static FileIdentity FromCharset(Encoding body, Encoding saving, string charsetName, byte[] preamble) =>
        new(TextFileCodec.Strict(body), TextFileCodec.Strict(saving), charsetName, preamble);

    /// <summary>
    /// The BOM ladder, in the only order that works: UTF-8, then UTF-32 BEFORE UTF-16 — the
    /// UTF-32LE BOM (`FF FE 00 00`) starts with the UTF-16LE one, so testing UTF-16 first reads a
    /// UTF-32 file as UTF-16 and encodes everything appended to it twice as wide. Bytes with no BOM
    /// come back as plain `utf-8`: whether they really ARE UTF-8 is the strict probe's answer, and
    /// that question belongs to the caller, next to its `.editorconfig` fallback.
    /// </summary>
    public static FileIdentity FromBytes(byte[] bytes)
    {
        if (TextFileCodec.StartsWith(bytes, 0xEF, 0xBB, 0xBF))
            return FromCharset(new UTF8Encoding(false), new UTF8Encoding(true), "utf-8-bom", [0xEF, 0xBB, 0xBF]);

        if (TextFileCodec.StartsWith(bytes, 0xFF, 0xFE, 0x00, 0x00))
            return FromCharset(new UTF32Encoding(false, byteOrderMark: false), new UTF32Encoding(false, byteOrderMark: true), "utf-32le", [0xFF, 0xFE, 0x00, 0x00]);

        if (TextFileCodec.StartsWith(bytes, 0x00, 0x00, 0xFE, 0xFF))
            return FromCharset(new UTF32Encoding(true, byteOrderMark: false), new UTF32Encoding(true, byteOrderMark: true), "utf-32be", [0x00, 0x00, 0xFE, 0xFF]);

        if (TextFileCodec.StartsWith(bytes, 0xFF, 0xFE))
            return FromCharset(new UnicodeEncoding(false, byteOrderMark: false), new UnicodeEncoding(false, byteOrderMark: true), "utf-16le", [0xFF, 0xFE]);

        if (TextFileCodec.StartsWith(bytes, 0xFE, 0xFF))
            return FromCharset(new UnicodeEncoding(true, byteOrderMark: false), new UnicodeEncoding(true, byteOrderMark: true), "utf-16be", [0xFE, 0xFF]);

        return FromCharset(new UTF8Encoding(false), new UTF8Encoding(false), "utf-8", []);
    }
}

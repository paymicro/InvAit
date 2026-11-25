using System.Text;

namespace ToolCore.Tests;

/// <summary>
/// The shared codec: the strict-UTF-8 probe, the charset table, the ONE line-break algorithm, the
/// byte helpers and the identity a file is given. `FileWriter`, `FileUtils` and `EditApplier` all
/// delegate here, so a charset or a separator can no longer be answered differently per file.
/// </summary>
public class TextFileCodecTests
{
    private static readonly UTF8Encoding _utf8 = new(false);

    private static byte[] Utf8(string text) => _utf8.GetBytes(text);

    private static byte[] Concat(params byte[][] parts)
    {
        var bytes = new List<byte>();
        foreach (var part in parts)
            bytes.AddRange(part);
        return bytes.ToArray();
    }

    #region SplitEditText — parity with the EditApplier cases

    [Theory]
    [InlineData("a\nb\nc")]
    [InlineData("a\r\nb\r\nc")]
    [InlineData("a\rb\rc")]
    public void SplitEditText_EveryLineBreakKind_SplitsIntoLines(string text)
    {
        Assert.Equal(["a", "b", "c"], TextLines.SplitEditText(text));
    }

    [Fact]
    public void SplitEditText_MixedLineEndings_SplitsEveryLine()
    {
        // A search block that disagrees with the file must still meet it on the same line list.
        var lines = TextLines.SplitEditText("a\r\nb\nc\rd");

        Assert.Equal(["a", "b", "c", "d"], lines);
        Assert.DoesNotContain(lines, l => l.Contains('\r'));
    }

    [Theory]
    [InlineData("line2\n")]
    [InlineData("line2\r\n")]
    [InlineData("line2\r")]
    public void SplitEditText_SingleTrailingBreak_IsATerminator(string text)
    {
        // "line2\n" used to split into ["line2", ""] and then searched for a REAL empty line after
        // line2 — the edit failed on any file where the next line has content.
        Assert.Equal(["line2"], TextLines.SplitEditText(text));
    }

    [Fact]
    public void SplitEditText_TwoTrailingBreaks_KeepsOneEmptyLine()
    {
        // A SECOND trailing break is a line the model really did address.
        Assert.Equal(["a", ""], TextLines.SplitEditText("a\n\n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SplitEditText_NullOrEmpty_YieldsOneEmptyLine(string? text)
    {
        Assert.Equal([""], TextLines.SplitEditText(text));
    }

    #endregion

    #region TextLines.FromText — the only splitter

    [Fact]
    public void FromText_FinalBreakIsMetadataNotAnEmptyLine()
    {
        var text = TextLines.FromText("a\r\nb\r\n");

        Assert.Equal(["a", "b"], text.Lines);
        Assert.True(text.HasFinalNewLine);
    }

    [Fact]
    public void FromText_EmptyText_HasNoLinesAndNoFinalBreak()
    {
        // An EMPTY file has zero lines; ["", ] is the edit-text convention, not the file's state.
        var text = TextLines.FromText(string.Empty);

        Assert.Empty(text.Lines);
        Assert.False(text.HasFinalNewLine);
    }

    #endregion

    #region DetectSeparator / HasMixedLineEndings — one majority rule over bytes

    [Fact]
    public void DetectSeparator_MajorityLf_BeatsAPresentCrlf()
    {
        // The cross-host defect in one assertion: presence-based sniffing answered CRLF here,
        // the majority rule (which the VS Code reader already used) answers LF.
        var bytes = Utf8("a\r\nb\nc\nd\ne");

        Assert.Equal("\n", TextFileCodec.DetectSeparator(bytes, 0, _utf8));
        Assert.True(TextFileCodec.HasMixedLineEndings(bytes, 0, _utf8));
    }

    [Fact]
    public void DetectSeparator_TieBetweenCrlfAndLf_PrefersCrlf()
    {
        var bytes = Utf8("a\nb\r\nc");

        Assert.Equal("\r\n", TextFileCodec.DetectSeparator(bytes, 0, _utf8));
    }

    [Fact]
    public void DetectSeparator_TieBetweenLfAndCr_PrefersLf()
    {
        var bytes = Utf8("a\nb\rc");

        Assert.Equal("\n", TextFileCodec.DetectSeparator(bytes, 0, _utf8));
        Assert.True(TextFileCodec.HasMixedLineEndings(bytes, 0, _utf8));
    }

    [Fact]
    public void DetectSeparator_NoLineBreak_HasNoPreference()
    {
        // null, not "\n": the WRITER needs to know the file has no convention of its own so
        // `.editorconfig` may still supply one. Callers that need a value use "\n".
        Assert.Null(TextFileCodec.DetectSeparator(Utf8("abc"), 0, _utf8));
        Assert.False(TextFileCodec.HasMixedLineEndings(Utf8("abc"), 0, _utf8));
    }

    [Fact]
    public void DetectSeparator_SkipsThePreamble()
    {
        var bytes = Concat(new byte[] { 0xEF, 0xBB, 0xBF }, Utf8("a\r\nb"));

        Assert.Equal("\r\n", TextFileCodec.DetectSeparator(bytes, 3, _utf8));
    }

    [Fact]
    public void DetectSeparator_Utf32Le_CountsAWholeBreakAsOne()
    {
        // The UnitWidth UTF-32 arm: a break is 4 bytes wide, so scanning with a UTF-16 width of 2
        // counts `0D 00 00 00` as a lone CR plus a lone LF — a tie that answers "\n" instead of
        // the "\r\n" the file actually uses.
        var encoding = new UTF32Encoding(bigEndian: false, byteOrderMark: true);
        var bytes = Concat(encoding.GetPreamble(), encoding.GetBytes("a\r\nb\r\n"));
        var strict = TextFileCodec.Strict(encoding);

        Assert.Equal("\r\n", TextFileCodec.DetectSeparator(bytes, 4, strict));
        Assert.False(TextFileCodec.HasMixedLineEndings(bytes, 4, strict));
    }

    [Fact]
    public void DetectSeparator_Utf16Le_CountsAWholeBreakAsOne()
    {
        // A line break is 2 bytes wide in UTF-16: scanning for a single 0x0A byte would find half
        // of every character and count breaks that are not there.
        var encoding = new UnicodeEncoding(false, byteOrderMark: true);
        var bytes = Concat(encoding.GetPreamble(), encoding.GetBytes("a\r\nb\nc\nd"));

        Assert.Equal("\n", TextFileCodec.DetectSeparator(bytes, 2, TextFileCodec.Strict(encoding)));
        Assert.True(TextFileCodec.HasMixedLineEndings(bytes, 2, TextFileCodec.Strict(encoding)));
    }

    [Fact]
    public void DetectSeparator_Utf16LoneCr_IsFound()
    {
        var encoding = new UnicodeEncoding(false, byteOrderMark: true);
        var bytes = Concat(encoding.GetPreamble(), encoding.GetBytes("a\rb"));

        Assert.Equal("\r", TextFileCodec.DetectSeparator(bytes, 2, TextFileCodec.Strict(encoding)));
    }

    #endregion

    #region IsStrictUtf8

    [Theory]
    [InlineData("plain ascii")]
    [InlineData("привет")]
    [InlineData("emoji 🙂 and text")]
    public void IsStrictUtf8_ValidText_IsAccepted(string text)
    {
        Assert.True(TextFileCodec.IsStrictUtf8(Utf8(text), complete: true));
    }

    [Fact]
    public void IsStrictUtf8_EmptyAndAscii_AreAccepted()
    {
        Assert.True(TextFileCodec.IsStrictUtf8(Array.Empty<byte>(), complete: true));
        Assert.True(TextFileCodec.IsStrictUtf8([0x61, 0x62, 0x63], complete: true));
    }

    [Fact]
    public void IsStrictUtf8_Latin1HighByte_IsRejected()
    {
        // This is the byte that used to become U+FFFD and then EF BF BD on disk.
        Assert.False(TextFileCodec.IsStrictUtf8([0x63, 0x61, 0x66, 0xE9], complete: true));
    }

    [Fact]
    public void IsStrictUtf8_TruncatedTrailingCharacter_DependsOnCompleteness()
    {
        // A 64 KB sniff window may end inside a multi-byte character: that is not invalid UTF-8,
        // it is an incomplete buffer — and the probe must not refuse a valid file because of it.
        var bytes = Utf8("ж");
        var cut = bytes.Take(bytes.Length - 1).ToArray();

        Assert.True(TextFileCodec.IsStrictUtf8(cut, complete: false));
        Assert.False(TextFileCodec.IsStrictUtf8(cut, complete: true));
    }

    [Fact]
    public void IsStrictUtf8_LargeValidBuffer_IsFullyProbed()
    {
        // The probe works in fixed-size chunks; a buffer far larger than one chunk must not be
        // accepted just because its first chunk is fine.
        var bytes = Utf8(new string('ж', 5000));
        Assert.True(TextFileCodec.IsStrictUtf8(bytes, complete: true));

        bytes[bytes.Length - 1] = 0xFF;
        Assert.False(TextFileCodec.IsStrictUtf8(bytes, complete: true));
    }

    #endregion

    #region CreateEncoding — the charset table

    [Fact]
    public void CreateEncoding_UnknownName_IsNullAndNeverGuessed()
    {
        Assert.Null(TextFileCodec.CreateEncoding(null));
        Assert.Null(TextFileCodec.CreateEncoding("utf-9"));
        Assert.Null(TextFileCodec.CreateEncoding("cp1251"));
    }

    [Fact]
    public void CreateEncoding_Utf8WithoutBom_HasNoPreamble()
    {
        Assert.Empty(TextFileCodec.CreateEncoding("utf-8")!.GetPreamble());
    }

    [Fact]
    public void CreateEncoding_Utf8Bom_HasThePreamble()
    {
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, TextFileCodec.CreateEncoding("utf-8-bom")!.GetPreamble());
    }

    [Fact]
    public void CreateEncoding_Latin1_IsOneBytePerCharacter()
    {
        var latin1 = TextFileCodec.CreateEncoding("latin1")!;

        Assert.Equal(new byte[] { 0x63, 0x61, 0x66, 0xE9 }, latin1.GetBytes("café"));
        Assert.Empty(latin1.GetPreamble());
    }

    [Theory]
    [InlineData("utf-16le", new byte[] { 0xFF, 0xFE })]
    [InlineData("utf-16be", new byte[] { 0xFE, 0xFF })]
    public void CreateEncoding_Utf16_HasTheDeclaredByteOrder(string charset, byte[] expectedPreamble)
    {
        Assert.Equal(expectedPreamble, TextFileCodec.CreateEncoding(charset)!.GetPreamble());
    }

    #endregion

    #region Small helpers

    [Fact]
    public void NormalizeEol_RewritesEveryKind()
    {
        Assert.Equal("a\r\nb\r\nc", TextFileCodec.NormalizeEol("a\r\nb\nc", "\r\n"));
        Assert.Equal("a\nb\nc", TextFileCodec.NormalizeEol("a\r\nb\rc", "\n"));
        Assert.Equal("a\rb\rc", TextFileCodec.NormalizeEol("a\nb\r\nc", "\r"));
    }

    [Theory]
    [InlineData("\r\n", "crlf")]
    [InlineData("\n", "lf")]
    [InlineData("\r", "cr")]
    public void SeparatorName_NamesTheThreeKinds(string separator, string expected)
    {
        Assert.Equal(expected, TextFileCodec.SeparatorName(separator));
    }

    [Fact]
    public void ByteHelpers_WorkOnRawBytes()
    {
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0x01, 0x02, 0x03 };

        Assert.True(TextFileCodec.StartsWith(bytes, 0x00, 0x01));
        Assert.False(TextFileCodec.StartsWith(bytes, 0x01, 0x02));
        Assert.False(TextFileCodec.StartsWith(bytes, 0x00, 0x01, 0x02, 0x03));

        Assert.True(TextFileCodec.EndsWith(bytes, [0x02, 0x03]));
        Assert.False(TextFileCodec.EndsWith(bytes, [0x01, 0x02, 0x03, 0x00]));
        Assert.False(TextFileCodec.EndsWith(bytes, Array.Empty<byte>()));
    }

    [Theory]
    [InlineData("a\r\n", true)]
    [InlineData("a\n", true)]
    [InlineData("a\r", true)]
    [InlineData("a", false)]
    public void EndsWithLineBreak_AcceptsAnyBreakNotOnlyTheDominantOne(string text, bool expected)
    {
        // The append/prepend glue rule. Testing the DOMINANT separator instead put a blank line
        // into a file whose last break was a different kind, and made this host disagree with
        // the VS Code one.
        Assert.Equal(expected, TextFileCodec.EndsWithLineBreak(Utf8(text), _utf8));
    }

    [Fact]
    public void EndsWithLineBreak_Utf16_SeesTheWideFormAndNotAStrayByte()
    {
        // In UTF-16LE `\n` is `0A 00`: a byte-wise "ends with 0x0A" test would answer false
        // because the buffer ends with 0x00.
        var encoding = new UnicodeEncoding(false, byteOrderMark: false);
        var strict = TextFileCodec.Strict(encoding);

        Assert.True(TextFileCodec.EndsWithLineBreak(encoding.GetBytes("a\n"), strict));
        Assert.False(TextFileCodec.EndsWithLineBreak(encoding.GetBytes("a"), strict));
    }

    #endregion

    #region FileIdentity — BOM ladder and the TWO encodings

    [Fact]
    public void FileIdentity_Utf8Bom_KeepsThePreambleForSavingOnly()
    {
        var identity = FileIdentity.FromBytes([0xEF, 0xBB, 0xBF, 0x61]);

        Assert.True(identity.HasBom);
        Assert.Equal("utf-8-bom", identity.CharsetName);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, identity.Preamble);
        // The BODY codec must stay clean: the caller splices Preamble itself.
        Assert.Empty(identity.Encoding.GetPreamble());
        // SaveFileAsync writes through StreamWriter, which emits exactly this.
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, identity.SavingEncoding.GetPreamble());
    }

    [Fact]
    public void FileIdentity_Utf32Le_IsTestedBeforeUtf16()
    {
        // FF FE 00 00 also starts with the UTF-16LE BOM: reading a UTF-32 file as UTF-16 encodes
        // everything appended to it twice as wide.
        var identity = FileIdentity.FromBytes([0xFF, 0xFE, 0x00, 0x00, 0x61, 0x00, 0x00, 0x00]);

        Assert.Equal("utf-32le", identity.CharsetName);
        Assert.Equal(4, identity.Preamble.Length);
    }

    [Fact]
    public void FileIdentity_Utf32Be_IsRecognised()
    {
        var identity = FileIdentity.FromBytes([0x00, 0x00, 0xFE, 0xFF]);

        Assert.Equal("utf-32be", identity.CharsetName);
        Assert.Equal(new byte[] { 0x00, 0x00, 0xFE, 0xFF }, identity.Preamble);
    }

    [Fact]
    public void FileIdentity_Utf16Le_KeepsTheBomForSavingOnly()
    {
        var identity = FileIdentity.FromBytes([0xFF, 0xFE, 0x61, 0x00]);

        Assert.Equal("utf-16le", identity.CharsetName);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, identity.Preamble);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, identity.SavingEncoding.GetPreamble());
        Assert.Empty(identity.Encoding.GetPreamble());
        Assert.Equal("a", identity.Encoding.GetString([0x61, 0x00]));
    }

    [Fact]
    public void FileIdentity_Utf16Be_IsRecognised()
    {
        var identity = FileIdentity.FromBytes([0xFE, 0xFF, 0x00, 0x61]);

        Assert.Equal("utf-16be", identity.CharsetName);
        Assert.Equal("a", identity.Encoding.GetString([0x00, 0x61]));
    }

    [Fact]
    public void FileIdentity_NoBom_IsPlainUtf8AndDeclaresNothing()
    {
        // No BOM means "nothing is decided here": whether these bytes really are UTF-8 is the
        // strict probe's answer, not this factory's guess.
        var identity = FileIdentity.FromBytes(Utf8("abc"));

        Assert.False(identity.HasBom);
        Assert.Equal("utf-8", identity.CharsetName);
        Assert.Empty(identity.Preamble);
        Assert.Empty(identity.SavingEncoding.GetPreamble());
    }

    [Fact]
    public void FileIdentity_FromCharset_TakesTheGivenPreambleAndThrowsOnLoss()
    {
        // A charset taken from `.editorconfig` for a file that has no BOM of its own: the prefix
        // stays the FILE'S bytes (empty), and the codec refuses to lose a character instead of
        // writing '?'.
        var latin1 = Encoding.GetEncoding(28591);
        var identity = FileIdentity.FromCharset(latin1, latin1, "latin1", Array.Empty<byte>());

        Assert.False(identity.HasBom);
        Assert.Equal("latin1", identity.CharsetName);
        Assert.Equal(new byte[] { 0xE9 }, identity.Encoding.GetBytes("é"));
        Assert.Throws<EncoderFallbackException>(() => { identity.Encoding.GetBytes("привет"); });
    }

    #endregion
}

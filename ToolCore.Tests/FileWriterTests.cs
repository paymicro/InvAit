using System.IO.Abstractions.TestingHelpers;
using System.Text;

namespace ToolCore.Tests;

/// <summary>
/// `create_file` writer: `.editorconfig` encoding, preservation of an existing file's bytes and
/// the append / prepend modes. Covers the whole tool logic that used to live in the VS host and
/// could not be tested there (`File.WriteAllText(..., Encoding.UTF8)` = UTF-8 WITH a BOM).
/// </summary>
public class FileWriterTests
{
    private const string _root = @"C:\ws";
    private const string _path = _root + @"\src\file.txt";

    private static MockFileSystem Tree(string? content = null, string? editorConfig = null, string path = _path, byte[]? raw = null)
    {
        var data = new Dictionary<string, MockFileData> { [_root + @"\.gitkeep"] = new MockFileData(string.Empty) };
        // `raw` bypasses the string overload: a cp1251/latin1 file holds bytes that are not text here.
        if (raw != null)
            data[path] = new MockFileData(raw);
        else if (content != null)
            data[path] = new MockFileData(content);
        if (editorConfig != null)
            data[_root + @"\.editorconfig"] = new MockFileData(editorConfig);
        return new MockFileSystem(data);
    }

    private static byte[] Bytes(MockFileSystem fs, string path = _path) => fs.File.ReadAllBytes(path);

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static byte[] Concat(params byte[][] parts)
    {
        var bytes = new List<byte>();
        foreach (var part in parts)
            bytes.AddRange(part);
        return bytes.ToArray();
    }

    #region New file — encoding from .editorconfig

    [Fact]
    public async Task Write_NewFileWithoutEditorConfig_IsUtf8WithoutBom()
    {
        // The regression: this exact call used to produce EF BB BF (Encoding.UTF8 writes a BOM).
        var fs = Tree();

        var result = await new FileWriter(fs).WriteAsync(_path, "class A { }", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(HasUtf8Bom(Bytes(fs)));
        Assert.Equal("class A { }", await fs.File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken));
        Assert.Contains("Encoding: utf-8", result.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_NewFile_EditorConfigUtf8Bom_WritesBom()
    {
        var fs = Tree(editorConfig: "root = true\n[*]\ncharset = utf-8-bom\n");

        var result = await new FileWriter(fs).WriteAsync(_path, "x", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.True(HasUtf8Bom(Bytes(fs)));
        Assert.Contains("utf-8-bom", result.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_NewFile_EditorConfigCrlf_NormalisesContentLineEndings()
    {
        var fs = Tree(editorConfig: "root = true\n[*]\nend_of_line = crlf\n");

        await new FileWriter(fs).WriteAsync(_path, "a\nb\nc", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.Equal("a\r\nb\r\nc", await fs.File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Write_NewFile_NoEditorConfig_KeepsContentVerbatim()
    {
        // No tree rule: guessing the platform newline would be a change the caller never asked for.
        var fs = Tree();

        await new FileWriter(fs).WriteAsync(_path, "a\nb", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.Equal("a\nb", await fs.File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Write_NewFile_EditorConfigLatin1_EncodesSingleByte()
    {
        var fs = Tree(editorConfig: "root = true\n[*]\ncharset = latin1\n");

        var result = await new FileWriter(fs).WriteAsync(_path, "café", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        // 63 61 66 E9 — one byte per character, `é` is NOT the UTF-8 pair C3 A9.
        Assert.Equal(new byte[] { 0x63, 0x61, 0x66, 0xE9 }, Bytes(fs));
        Assert.Contains("Encoding: latin1.", result.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_CreatesMissingDirectories()
    {
        var fs = Tree();

        var result = await new FileWriter(fs).WriteAsync(@"C:\ws\deep\nested\file.txt", "x", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(fs.File.Exists(@"C:\ws\deep\nested\file.txt"));
    }

    #endregion

    #region Existing file — bytes win

    [Fact]
    public async Task Write_OverwriteExistingWithBom_KeepsBom()
    {
        var fs = Tree(content: "\uFEFFold");

        await new FileWriter(fs).WriteAsync(_path, "new", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.True(HasUtf8Bom(Bytes(fs)));
        Assert.Equal("new", await fs.File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Write_OverwriteExistingWithoutBom_DoesNotGainOne()
    {
        // Even when .editorconfig asks for utf-8-bom: an existing file is not silently re-encoded,
        // the mismatch is reported instead.
        var fs = Tree(content: "old", editorConfig: "root = true\n[*]\ncharset = utf-8-bom\n");

        var result = await new FileWriter(fs).WriteAsync(_path, "new", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.False(HasUtf8Bom(Bytes(fs)));
        // This used to say "(existing bytes preserved)", which was a lie: the whole file was
        // decoded and re-encoded. The note now names what was actually written.
        Assert.Contains("requires charset 'utf-8-bom' but the file is 'utf-8'", result.Payload, StringComparison.Ordinal);
        Assert.Contains("The written content used 'utf-8'.", result.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("existing bytes preserved", result.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_OverwriteExistingCrlf_KeepsCrlf()
    {
        var fs = Tree(content: "a\r\nb");

        await new FileWriter(fs).WriteAsync(_path, "x\ny", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.Equal("x\r\ny", await fs.File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Write_OverwriteExistingLfWithEditorConfigCrlf_KeepsFileEndingsAndReports()
    {
        var fs = Tree(content: "a\nb", editorConfig: "root = true\n[*]\nend_of_line = crlf\n");

        var result = await new FileWriter(fs).WriteAsync(_path, "x\ny", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.Equal("x\ny", await fs.File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken));
        Assert.Contains("end_of_line 'crlf'", result.Payload, StringComparison.Ordinal);
    }

    #endregion

    #region Existing file — a guess never rewrites bytes

    [Theory]
    [InlineData(FileWriteMode.Overwrite)]
    [InlineData(FileWriteMode.Append)]
    [InlineData(FileWriteMode.Prepend)]
    public async Task Write_NonUtf8WithoutBomAndNoCharset_IsRefusedAndWritesNothing(FileWriteMode mode)
    {
        // The data-loss bug: the file was decoded as UTF-8 (E9 -> U+FFFD), so the whole file was
        // rewritten with EF BF BD in place of its bytes while the report claimed success.
        var original = new byte[] { 0x63, 0x61, 0x66, 0xE9 };
        var fs = Tree(raw: original);

        var result = await new FileWriter(fs).WriteAsync(_path, "new", mode, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(original, Bytes(fs));
        Assert.Contains("is not valid UTF-8 and has no BOM", result.Payload, StringComparison.Ordinal);
        Assert.Contains("Set charset in .editorconfig, or edit the file with bash.", result.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_NonUtf8File_WithUnsupportedDeclaredCharset_IsRefusedAndNamesIt()
    {
        // A declared charset the writer does not implement is NOT a licence to guess: the refusal
        // says which value is unusable instead of claiming the tree declares nothing.
        var original = new byte[] { 0x63, 0x61, 0x66, 0xE9 };
        var fs = Tree(raw: original, editorConfig: "root = true\n[*]\ncharset = cp1251\n");

        var result = await new FileWriter(fs).WriteAsync(_path, "new", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(original, Bytes(fs));
        Assert.Contains("declares charset 'cp1251', which is not supported", result.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_Append_ToExistingLatin1File_PreservesItsBytes()
    {
        // The same file, but the tree NAMES the charset: the old E9 byte survives and only the new
        // text is encoded — never EF BF BD.
        var fs = Tree(raw: new byte[] { 0x63, 0x61, 0x66, 0xE9 }, editorConfig: "root = true\n[*]\ncharset = latin1\n");

        var result = await new FileWriter(fs).WriteAsync(_path, "bar", FileWriteMode.Append, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(new byte[] { 0x63, 0x61, 0x66, 0xE9, 0x0A, 0x62, 0x61, 0x72 }, Bytes(fs));
        // The encoding came from the tree, so say exactly that — and never that it is unsupported.
        Assert.Contains("Encoding: latin1 (from .editorconfig).", result.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("is not supported", result.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFFFD", result.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_Overwrite_ExistingLatin1File_StaysLatin1()
    {
        // A file whose bytes are NOT valid UTF-8 plus a tree that names the charset: the content is
        // encoded with `latin1`, so `é` stays one byte instead of becoming the UTF-8 pair C3 A9.
        var fs = Tree(raw: new byte[] { 0xE9 }, editorConfig: "root = true\n[*]\ncharset = latin1\n");

        var result = await new FileWriter(fs).WriteAsync(_path, "café", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(new byte[] { 0x63, 0x61, 0x66, 0xE9 }, Bytes(fs));
        Assert.Contains("Encoding: latin1 (from .editorconfig).", result.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_Append_ToUtf8BomFile_KeepsThePreambleAndEveryByte()
    {
        var fs = Tree(raw: Concat(new byte[] { 0xEF, 0xBB, 0xBF }, Encoding.UTF8.GetBytes("a\r\nb")));

        var result = await new FileWriter(fs).WriteAsync(_path, "c", FileWriteMode.Append, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(Concat(new byte[] { 0xEF, 0xBB, 0xBF }, Encoding.UTF8.GetBytes("a\r\nb\r\nc")), Bytes(fs));
        // REVERSED: this used to demand `Line endings: crlf.` for a separator that was merely
        // SNIFFED from the file. `Line endings:` is printed only when .editorconfig ENFORCES
        // `end_of_line` (docs/dev/file-writes.md), so a sniffed value must stay silent.
        Assert.Contains("Encoding: utf-8-bom.", result.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Line endings:", result.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_Append_ToUtf16LeFile_IsByteExact()
    {
        var encoding = new UnicodeEncoding(false, byteOrderMark: true);
        var fs = Tree(raw: Concat(encoding.GetPreamble(), encoding.GetBytes("a\r\nb")));

        var result = await new FileWriter(fs).WriteAsync(_path, "c", FileWriteMode.Append, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(Concat(encoding.GetPreamble(), encoding.GetBytes("a\r\nb\r\nc")), Bytes(fs));
        // REVERSED for the same reason as the utf-8-bom case above: no `end_of_line` in the tree
        // means no `Line endings:` in the report, however confident the sniff is.
        Assert.Contains("Encoding: utf-16le.", result.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Line endings:", result.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_Append_ToUtf32LeFile_IsNotMistakenForUtf16()
    {
        // `FF FE 00 00` was read as a UTF-16LE BOM, so the appended text was encoded as UTF-16 and
        // the file stopped being decodable as UTF-32 at all.
        var encoding = new UTF32Encoding(false, byteOrderMark: true);
        var fs = Tree(raw: Concat(encoding.GetPreamble(), encoding.GetBytes("a")));

        var result = await new FileWriter(fs).WriteAsync(_path, "b", FileWriteMode.Append, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Contains("Encoding: utf-32le.", result.Payload, StringComparison.Ordinal);
        var bytes = Bytes(fs);
        Assert.Equal(encoding.GetPreamble(), bytes.Take(4).ToArray());
        Assert.Equal("a\nb", encoding.GetString(bytes, 4, bytes.Length - 4));
    }

    [Fact]
    public async Task Write_Overwrite_HugeFile_OnlySniffsItsHead()
    {
        // The body of an overwritten file is never read: 3 MB of text would be copied only to be
        // thrown away, and both decisions (preamble, separator) come from the first 64 KB.
        var fs = Tree(content: new string('x', 3 * 1024 * 1024), editorConfig: "root = true\n[*]\nend_of_line = crlf\n");

        var result = await new FileWriter(fs).WriteAsync(_path, "a\nb", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("a\r\nb", await fs.File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken));
        Assert.Contains("Line endings: crlf.", result.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_NewFile_Latin1CannotRepresentCyrillic_IsRefused()
    {
        // `charset = latin1` used to turn `привет` into `??????` and call that a success.
        var fs = Tree(editorConfig: "root = true\n[*]\ncharset = latin1\n");

        var result = await new FileWriter(fs).WriteAsync(_path, "привет", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(fs.File.Exists(_path));
        Assert.Contains("charset 'latin1'", result.Payload, StringComparison.Ordinal);
        Assert.Contains("The file was NOT modified.", result.Payload, StringComparison.Ordinal);
    }

    #endregion

    #region Report — it must name what was actually written

    [Fact]
    public async Task Write_NewFile_ReportIsExact()
    {
        var fs = Tree(editorConfig: "root = true\n[*]\ncharset = utf-8-bom\nend_of_line = crlf\n");

        var result = await new FileWriter(fs).WriteAsync(_path, "a\nb", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.Equal(@"File C:\ws\src\file.txt created successfully. Encoding: utf-8-bom. Line endings: crlf.", result.Payload);
    }

    [Fact]
    public async Task Write_NewFile_CharsetUnset_FallsBackToUtf8WithoutANote()
    {
        // `charset = unset` is not a charset: it clears the inherited value, so nothing is "not
        // supported" and the writer falls back to BOM-less UTF-8 silently.
        var fs = Tree(editorConfig: "root = true\n[*]\ncharset = latin1\n[*.txt]\ncharset = unset\n");

        var result = await new FileWriter(fs).WriteAsync(_path, "x", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(HasUtf8Bom(Bytes(fs)));
        Assert.Equal(@"File C:\ws\src\file.txt created successfully. Encoding: utf-8.", result.Payload);
    }

    [Fact]
    public async Task Write_NewFile_LeadingPreambleCharacter_IsRemovedForABomlessCharset()
    {
        // UTF-8 without a BOM plus a caller-supplied U+FEFF: keeping it writes EF BB BF and gives the
        // tree exactly the invisible prefix this writer exists to avoid.
        var fs = Tree();

        var result = await new FileWriter(fs).WriteAsync(_path, "\uFEFFa", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(new byte[] { 0x61 }, Bytes(fs));
        Assert.Contains("a leading U+FEFF was removed", result.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_NewFile_Utf8BomCharset_ContentPreambleDoesNotDoubleTheBom()
    {
        // The guard used to fire only when the target had NO preamble. With `charset = utf-8-bom`
        // the writer emits one BOM and the content carried a second: two U+FEFF in a row, which is
        // a stray character before the first C# token.
        var fs = Tree(editorConfig: "root = true\n[*]\ncharset = utf-8-bom\n");

        var result = await new FileWriter(fs).WriteAsync(_path, "\uFEFFa", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, 0x61 }, Bytes(fs));
        Assert.Contains("must not be repeated", result.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_ExistingBomFile_Overwrite_ContentPreambleDoesNotDoubleTheBom()
    {
        // Same hole on the existing-file path: the BOM comes from the file's own identity, so a
        // U+FEFF inside the content is always a SECOND one and no code below recognises it.
        var fs = Tree(raw: Concat(new byte[] { 0xEF, 0xBB, 0xBF }, Encoding.UTF8.GetBytes("old")));

        var result = await new FileWriter(fs).WriteAsync(_path, "\uFEFFnew", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(Concat(new byte[] { 0xEF, 0xBB, 0xBF }, Encoding.UTF8.GetBytes("new")), Bytes(fs));
        Assert.Contains("must not be repeated", result.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_ExistingFile_EndOfLineUnset_UsesTheFilesOwnSeparator()
    {
        var fs = Tree(content: "a\nb", editorConfig: "root = true\n[*]\nend_of_line = crlf\n[*.txt]\nend_of_line = unset\n");

        var result = await new FileWriter(fs).WriteAsync(_path, "x\ny", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("x\ny", await fs.File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken));
        // REVERSED: `Line endings: lf.` used to be printed for the SNIFFED separator even though
        // `end_of_line` is unset. Silence now means "the file's own endings went to disk".
        Assert.DoesNotContain("Line endings:", result.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Note:", result.Payload, StringComparison.Ordinal);
    }

    #endregion

    #region Line endings — one majority rule for both hosts

    [Fact]
    public async Task Write_Overwrite_FileWithMajorityLf_NormalisesToLf()
    {
        // Cross-host divergence: the presence-based sniff (CRLF first) rewrote this content with
        // CRLF while the VS Code reader answered LF for the same bytes. One CRLF against four LF
        // is an LF file that happens to contain one CRLF.
        var fs = Tree(content: "a\r\nb\nc\nd\ne");

        await new FileWriter(fs).WriteAsync(_path, "x\ny", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.Equal("x\ny", await fs.File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Write_Append_ToFileWithMajorityLf_UsesLfAsGlue()
    {
        // The glue must be the same separator the reader reports, otherwise append and edit_file
        // disagree about the very same file.
        var fs = Tree(content: "a\r\nb\nc\nd\ne");

        await new FileWriter(fs).WriteAsync(_path, "X", FileWriteMode.Append, TestContext.Current.CancellationToken);

        Assert.Equal("a\r\nb\nc\nd\ne\nX", await fs.File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Write_Append_ToFileWithTiedLineEndings_UsesCrlf()
    {
        // Tie rule: CRLF > LF > CR. One CRLF and one LF is a tie, and a tie must not depend on
        // which kind of break the scan happens to meet first.
        var fs = Tree(content: "a\nb\r\nc");

        await new FileWriter(fs).WriteAsync(_path, "X", FileWriteMode.Append, TestContext.Current.CancellationToken);

        Assert.Equal("a\nb\r\nc\r\nX", await fs.File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken));
    }

    #endregion

    #region Append / prepend

    [Fact]
    public async Task Write_Append_AddsToTheEnd()
    {
        var fs = Tree(content: "a\nb");

        var result = await new FileWriter(fs).WriteAsync(_path, "c\n", FileWriteMode.Append, TestContext.Current.CancellationToken);

        // The exact bytes: the old expectation laundered the result through Replace("\r\n", "\n")
        // and could not tell a CRLF file from an LF one.
        Assert.Equal("a\nb\nc\n", await fs.File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken));
        Assert.Contains("appended to", result.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_Append_ToFileWithoutFinalNewLine_InsertsSeparator()
    {
        // Otherwise the last old line and the first new one would merge into one.
        var fs = Tree(content: "a\r\nb");

        await new FileWriter(fs).WriteAsync(_path, "c", FileWriteMode.Append, TestContext.Current.CancellationToken);

        Assert.Equal("a\r\nb\r\nc", await fs.File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Write_Append_ToFileWithFinalNewLine_DoesNotInsertExtraBreak()
    {
        var fs = Tree(content: "a\r\nb\r\n");

        await new FileWriter(fs).WriteAsync(_path, "c", FileWriteMode.Append, TestContext.Current.CancellationToken);

        Assert.Equal("a\r\nb\r\nc", await fs.File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Write_Append_ToFileWhoseLastBreakIsNotTheDominantOne_InsertsNoBlankLine()
    {
        // CRLF is the majority, but the body already ends with a break (`\n`). The glue rule is
        // "no line break AT ALL at the end", not "not the dominant one": testing the dominant
        // separator put a blank line between `c` and `X`, and it was the shape where this host
        // disagreed with the VS Code one on the same input.
        var fs = Tree(content: "a\r\nb\r\nc\n");

        await new FileWriter(fs).WriteAsync(_path, "X", FileWriteMode.Append, TestContext.Current.CancellationToken);

        Assert.Equal("a\r\nb\r\nc\nX", await fs.File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Write_Prepend_AddsToTheStart()
    {
        var fs = Tree(content: "b\nc");

        var result = await new FileWriter(fs).WriteAsync(_path, "a\n", FileWriteMode.Prepend, TestContext.Current.CancellationToken);

        Assert.Equal("a\nb\nc", await fs.File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken));
        Assert.Contains("prepended to", result.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_Prepend_WithoutTrailingBreak_InsertsSeparator()
    {
        // A file with no line break at all gets `\n`, NOT Environment.NewLine: the old expectation
        // (`a\r\nb`) pinned the platform newline and diverged from the VS Code host.
        var fs = Tree(content: "b");

        await new FileWriter(fs).WriteAsync(_path, "a", FileWriteMode.Prepend, TestContext.Current.CancellationToken);

        Assert.Equal("a\nb", await fs.File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Write_Prepend_ToUtf8BomFile_KeepsThePreambleFirst()
    {
        // `chunk + existing bytes` would bury the BOM in the middle of the file, where nothing
        // recognises it and every reader shows it as stray text. The preamble must stay FIRST.
        var fs = Tree(raw: Concat(new byte[] { 0xEF, 0xBB, 0xBF }, Encoding.UTF8.GetBytes("b\nc")));

        var result = await new FileWriter(fs).WriteAsync(_path, "a\n", FileWriteMode.Prepend, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(Concat(new byte[] { 0xEF, 0xBB, 0xBF }, Encoding.UTF8.GetBytes("a\nb\nc")), Bytes(fs));
    }

    [Fact]
    public async Task Write_Prepend_ToCrlfFile_InsertsCrlf()
    {
        // The same join, in the file's OWN separator — sniffed from its bytes, not from the host.
        var fs = Tree(content: "b\r\nc");

        await new FileWriter(fs).WriteAsync(_path, "a", FileWriteMode.Prepend, TestContext.Current.CancellationToken);

        Assert.Equal("a\r\nb\r\nc", await fs.File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Write_AppendToEmptyContent_IsANoOp()
    {
        var fs = Tree(content: "a");

        var result = await new FileWriter(fs).WriteAsync(_path, "", FileWriteMode.Append, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        // Nothing is written at all: the payload must say so instead of claiming an append.
        Assert.Contains("the content was empty and the file was not modified", result.Payload, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 0x61 }, Bytes(fs));
    }

    [Fact]
    public async Task Write_PrependEmptyContent_IsANoOp()
    {
        var fs = Tree(content: "a");

        var result = await new FileWriter(fs).WriteAsync(_path, "", FileWriteMode.Prepend, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Contains("was not modified", result.Payload, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 0x61 }, Bytes(fs));
    }

    [Fact]
    public async Task Write_Prepend_IntoEmptyFile_WritesTheContentVerbatim()
    {
        // An empty file has no separator to join to, so none is added: `a\nb` in, `a\nb` out.
        var fs = Tree(content: string.Empty);

        var result = await new FileWriter(fs).WriteAsync(_path, "a\nb", FileWriteMode.Prepend, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("a\nb", await fs.File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Write_Append_ToFileWithoutAnyLineBreak_UsesLfNotThePlatformSeparator()
    {
        // `Environment.NewLine` used to fill this gap, so the SAME call produced different bytes
        // on Windows and in the VS Code host.
        var fs = Tree(content: "a");

        var result = await new FileWriter(fs).WriteAsync(_path, "b", FileWriteMode.Append, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("a\nb", await fs.File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("Line endings:", result.Payload, StringComparison.Ordinal);
    }

    #endregion

    #region Mode parsing

    [Theory]
    [InlineData(null, FileWriteMode.Overwrite)]
    [InlineData("", FileWriteMode.Overwrite)]
    [InlineData("overwrite", FileWriteMode.Overwrite)]
    [InlineData("APPEND", FileWriteMode.Append)]
    [InlineData(" append ", FileWriteMode.Append)]
    [InlineData("prepend", FileWriteMode.Prepend)]
    public void ParseMode_AcceptsTheDocumentedValues(string? mode, FileWriteMode expected)
    {
        Assert.Equal(expected, FileWriter.ParseMode(mode));
    }

    [Fact]
    public async Task Write_UnknownMode_IsAPerCallFailure()
    {
        var fs = Tree();

        var result = await new FileWriter(fs).WriteAsync(_path, "x", "sideways", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("Unknown mode", result.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_AppendAContentThatIsOnlyAFeff_IsANoOp()
    {
        // The U+FEFF strip runs BEFORE the empty-content guard so the guard sees an EMPTY body.
        // It used to run after: the guard passed on "\uFEFF", the chunk encoded to nothing, and the
        // append still wrote the glue — the file grew by one line break and the payload said
        // "appended to successfully".
        var fs = Tree("a");

        var result = await new FileWriter(fs).WriteAsync(_path, "\uFEFF", FileWriteMode.Append, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(new byte[] { 0x61 }, Bytes(fs));
        Assert.Contains("was not modified", result.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_ExistingFile_DeclaredSeparatorTheFileDoesNotUse_IsNotReportedAsApplied()
    {
        // `Line endings:` states the rule THIS write enforced. Printing the SNIFFED value next to
        // ".editorconfig requires 'crlf' but the file uses 'lf'" made the payload contradict itself,
        // and the doc says a sniffed separator is the file's own state, not a rule this write applied.
        var fs = Tree("a\nb", "[*]\nend_of_line = crlf\n");

        var result = await new FileWriter(fs).WriteAsync(_path, "x\ny", FileWriteMode.Overwrite, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.DoesNotContain("Line endings:", result.Payload, StringComparison.Ordinal);
        Assert.Contains("requires end_of_line 'crlf'", result.Payload, StringComparison.Ordinal);
    }

    #endregion
}

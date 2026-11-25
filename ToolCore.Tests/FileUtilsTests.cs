using System.IO.Abstractions.TestingHelpers;
using System.Text;

namespace ToolCore.Tests;

public class FileUtilsTests
{
    private const string _targetPath = @"C:\data\file.txt";
    private const string _targetDirectory = @"C:\data";

    private static byte[] Concat(params byte[][] parts)
    {
        var bytes = new List<byte>();
        foreach (var part in parts)
            bytes.AddRange(part);
        return bytes.ToArray();
    }

    /// <summary>Counts EF BB BF occurrences, so "exactly one BOM" is asserted as a number, not by prefix luck.</summary>
    private static int CountUtf8Boms(byte[] bytes)
    {
        var count = 0;
        for (var i = 0; i + 2 < bytes.Length; i++)
        {
            if (bytes[i] == 0xEF && bytes[i + 1] == 0xBB && bytes[i + 2] == 0xBF)
                count++;
        }

        return count;
    }

    [Fact]
    public async Task ReadFileWithMetadataAsync_MissingFile_ReportsNotFound()
    {
        // A missing file used to be indistinguishable from an EMPTY one: the caller got zero
        // lines and no reason, reported "oldStr not found" for every edit of that path.
        var fileSystem = new MockFileSystem();
        var utils = new FileUtils(fileSystem);

        var result = await utils.ReadFileWithMetadataAsync(_targetPath, TestContext.Current.CancellationToken);

        Assert.Equal(FileReadStatus.NotFound, result.Status);
        Assert.Equal("file doesn't exist", result.SkipReason);
        Assert.Empty(result.Lines);
        Assert.False(result.HasFinalNewLine);
    }

    [Fact]
    public async Task ReadFileWithMetadataAsync_FileAboveTwoMegabytes_ReportsTooLarge()
    {
        // 3 MB used to come back as an EMPTY line list with no error channel at all: the host then
        // saw search.Count > target.Count and told the model "oldStr not found" — for every edit.
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(_targetPath, new MockFileData(new string('a', 3 * 1024 * 1024)));
        var utils = new FileUtils(fileSystem);

        var result = await utils.ReadFileWithMetadataAsync(_targetPath, TestContext.Current.CancellationToken);

        Assert.Equal(FileReadStatus.TooLarge, result.Status);
        Assert.Empty(result.Lines);
        Assert.NotNull(result.SkipReason);
        Assert.Contains("2 MB", result.SkipReason, StringComparison.Ordinal);
        // The size itself is reported so the model learns WHY the file is out of reach.
        Assert.Contains("3.0 MB", result.SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFileWithMetadataAsync_FileWithNulByteInFirstKilobyte_ReportsBinary()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(_targetPath, new MockFileData(new byte[] { 0x61, 0x62, 0x00, 0x63, 0x64 }));
        var utils = new FileUtils(fileSystem);

        var result = await utils.ReadFileWithMetadataAsync(_targetPath, TestContext.Current.CancellationToken);

        Assert.Equal(FileReadStatus.Binary, result.Status);
        Assert.Empty(result.Lines);
        Assert.Equal("file looks binary (NUL byte in the first 1 KB)", result.SkipReason);
    }

    [Fact]
    public async Task ReadFileWithMetadataAsync_EmptyFile_IsOkAndDistinguishableFromSkippedFile()
    {
        // An empty file is a legitimate read: Status Ok, no lines, no reason — the caller can now
        // tell it apart from "this file was not read".
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(_targetPath, new MockFileData(Array.Empty<byte>()));
        var utils = new FileUtils(fileSystem);

        var result = await utils.ReadFileWithMetadataAsync(_targetPath, TestContext.Current.CancellationToken);

        Assert.Equal(FileReadStatus.Ok, result.Status);
        Assert.Null(result.SkipReason);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public async Task ReadFileWithMetadataAsync_NoLineBreakInFile_SeparatorIsLf()
    {
        // Platform determinism: Environment.NewLine would make the same file read differently on
        // Windows and on Linux and flip "the file did not change" comparisons.
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(_targetPath, new MockFileData("JustOneLine"));
        var utils = new FileUtils(fileSystem);

        var result = await utils.ReadFileWithMetadataAsync(_targetPath, TestContext.Current.CancellationToken);

        Assert.Equal(FileReadStatus.Ok, result.Status);
        Assert.Equal("\n", result.Separator);
        var singleLine = Assert.Single(result.Lines);
        Assert.Equal("JustOneLine", singleLine);
        Assert.False(result.HasFinalNewLine);
    }

    [Theory]
    [InlineData("a\r\nb\r\n", false, "\r\n")]
    [InlineData("a\nb\n", false, "\n")]
    [InlineData("a\rb\r", false, "\r")]
    [InlineData("a\r\nb\nc\r\nd", true, "\r\n")]
    // REVERSED: this used to say "priority order, not most frequent". The rule is MAJORITY now
    // (the one algorithm both hosts use); one CRLF against one LF is a TIE, and ties go CRLF.
    [InlineData("a\nb\r\nc", true, "\r\n")]
    public async Task ReadFileWithMetadataAsync_ReportsMixedLineEndings(string text, bool expectedMixed, string expectedSeparator)
    {
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(_targetPath, new MockFileData(text));
        var utils = new FileUtils(fileSystem);

        var result = await utils.ReadFileWithMetadataAsync(_targetPath, TestContext.Current.CancellationToken);

        Assert.Equal(FileReadStatus.Ok, result.Status);
        Assert.Equal(expectedMixed, result.HasMixedLineEndings);
        Assert.Equal(expectedSeparator, result.Separator);
    }

    [Fact]
    public async Task ReadFileWithMetadataAsync_MajorityLineEnding_BeatsTheFirstBreak()
    {
        // Cross-host divergence: the presence-based sniff answered CRLF here while the VS Code
        // reader answered LF for the same bytes, so the two hosts rewrote one file differently.
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(_targetPath, new MockFileData("a\r\nb\nc\nd\ne"));
        var utils = new FileUtils(fileSystem);

        var result = await utils.ReadFileWithMetadataAsync(_targetPath, TestContext.Current.CancellationToken);

        Assert.Equal(FileReadStatus.Ok, result.Status);
        Assert.Equal("\n", result.Separator);
        Assert.True(result.HasMixedLineEndings);
        Assert.Equal(["a", "b", "c", "d", "e"], result.Lines);
    }

    [Fact]
    public async Task ReadFileWithMetadataAsync_NonUtf8WithoutBomAndNoCharset_ReportsBinaryAndKeepsTheBytes()
    {
        // THE data-loss bug of `edit_file`: this file used to be decoded as UTF-8 with U+FFFD
        // replacements, and the edit then SAVED those replacements over the original high bytes.
        // A read must refuse the file instead, so no write can ever follow.
        var original = new byte[] { 0xCF, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2 }; // "Привет" in cp1251
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(_targetPath, new MockFileData(original));
        var utils = new FileUtils(fileSystem);

        var result = await utils.ReadFileWithMetadataAsync(_targetPath, TestContext.Current.CancellationToken);

        Assert.Equal(FileReadStatus.Binary, result.Status);
        Assert.False(string.IsNullOrEmpty(result.SkipReason));
        Assert.Contains("not valid UTF-8", result.SkipReason, StringComparison.Ordinal);
        Assert.Contains("declares no charset", result.SkipReason, StringComparison.Ordinal);
        Assert.Empty(result.Lines);
        // The read itself wrote nothing back.
        Assert.Equal(original, await fileSystem.File.ReadAllBytesAsync(_targetPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadFileWithMetadataAsync_NonUtf8WithDeclaredLatin1_DecodesAndRoundTripsByteExact()
    {
        // The same bytes, but the tree NAMES the charset: the file becomes editable, and a
        // read -> save round trip must not change a single byte (latin1 maps all 256 values both
        // ways, and it has no preamble to add).
        var original = new byte[] { 0xCF, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2, 0x0A };
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(_targetPath, new MockFileData(original));
        fileSystem.AddFile(@"C:\.editorconfig", new MockFileData("root = true\n[*]\ncharset = latin1\n"));
        var utils = new FileUtils(fileSystem);

        var result = await utils.ReadFileWithMetadataAsync(_targetPath, TestContext.Current.CancellationToken);

        Assert.Equal(FileReadStatus.Ok, result.Status);
        Assert.Null(result.SkipReason);
        Assert.Equal("Ïðèâåò", Assert.Single(result.Lines));
        Assert.True(result.HasFinalNewLine);

        await utils.SaveFileAsync(_targetPath, result.Lines, result.Encoding, result.Separator, result.HasFinalNewLine);

        Assert.Equal(original, await fileSystem.File.ReadAllBytesAsync(_targetPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadFileWithMetadataAsync_Utf8BomFile_ReadSaveRoundTripKeepsExactlyOneBom()
    {
        // The BOM must round-trip: the encoding handed back is used by SaveFileAsync, which writes
        // through a StreamWriter and therefore emits GetPreamble(). A BOM-less body codec returned
        // here would silently drop the mark on the first edit; adding it twice is equally wrong.
        var original = Concat(new byte[] { 0xEF, 0xBB, 0xBF }, new UTF8Encoding(false).GetBytes("a\r\nb\r\n"));
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(_targetPath, new MockFileData(original));
        var utils = new FileUtils(fileSystem);

        var result = await utils.ReadFileWithMetadataAsync(_targetPath, TestContext.Current.CancellationToken);

        Assert.Equal(FileReadStatus.Ok, result.Status);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, result.Encoding.GetPreamble());
        Assert.Equal(["a", "b"], result.Lines);

        await utils.SaveFileAsync(_targetPath, result.Lines, result.Encoding, result.Separator, result.HasFinalNewLine);

        var bytes = await fileSystem.File.ReadAllBytesAsync(_targetPath, TestContext.Current.CancellationToken);
        Assert.Equal(original, bytes);
        Assert.Equal(1, CountUtf8Boms(bytes));
    }

    [Fact]
    public async Task ReadFileWithMetadataAsync_MixedLineEndings_SplitsEveryLine()
    {
        var fileSystem = new MockFileSystem();
        var mixedText = "FirstLine\r\nSecondLine\nThirdLine";
        fileSystem.AddFile(_targetPath, new MockFileData(mixedText));

        var utils = new FileUtils(fileSystem);

        var result = await utils.ReadFileWithMetadataAsync(_targetPath, TestContext.Current.CancellationToken);

        Assert.Equal(FileReadStatus.Ok, result.Status);
        Assert.Equal(["FirstLine", "SecondLine", "ThirdLine"], result.Lines);
        Assert.Equal("\r\n", result.Separator);
        Assert.False(result.HasFinalNewLine);
        Assert.True(result.HasMixedLineEndings);
    }

    [Theory]
    [InlineData("\r\n", true)]
    [InlineData("\r\n", false)]
    [InlineData("\n", true)]
    [InlineData("\n", false)]
    public async Task SaveAndRead_ShouldPreserveLineEndingsAndStructure(string originalSeparator, bool originalHasFinalNewLine)
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        var utils = new FileUtils(fileSystem);
        var sourceLines = new List<string> { "Строка 1", "Строка 2", "Строка 3" };
        var utf8WithoutBom = new UTF8Encoding(false);

        // Act & Assert 1: Сохраняем в виртуальную ФС
        await utils.SaveFileAsync(_targetPath, sourceLines, utf8WithoutBom, originalSeparator, originalHasFinalNewLine);

        // Act 2: Читаем обратно
        var result = await utils.ReadFileWithMetadataAsync(_targetPath, TestContext.Current.CancellationToken);

        // Assert 2: Проверяем идентичность метаданных
        Assert.Equal(FileReadStatus.Ok, result.Status);
        Assert.Null(result.SkipReason);
        Assert.Equal(sourceLines, result.Lines);
        Assert.Equal(originalSeparator, result.Separator);
        Assert.Equal(originalHasFinalNewLine, result.HasFinalNewLine);
        Assert.False(result.HasMixedLineEndings);
    }

    [Fact]
    public async Task ReadFileWithMetadataAsync_ShouldDetectUtf8WithBom()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        var utils = new FileUtils(fileSystem);
        var sourceLines = new List<string> { "Текст с BOM" };
        var encodingWithBom = new UTF8Encoding(true);

        await utils.SaveFileAsync(_targetPath, sourceLines, encodingWithBom, "\r\n", true);

        // Act
        var result = await utils.ReadFileWithMetadataAsync(_targetPath, TestContext.Current.CancellationToken);

        // Assert
        // Сравнение преамбул (массива байтов BOM) гарантирует точную идентификацию маркера
        Assert.Equal(encodingWithBom.GetPreamble(), result.Encoding.GetPreamble());
        Assert.Equal(sourceLines, result.Lines);
    }

    [Fact]
    public async Task ReadFileWithMetadataAsync_ShouldDetectUtf8WithoutBom()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        var utils = new FileUtils(fileSystem);
        var sourceLines = new List<string> { "Текст без BOM" };
        var encodingWithoutBom = new UTF8Encoding(false);

        await utils.SaveFileAsync(_targetPath, sourceLines, encodingWithoutBom, "\n", false);

        // Act
        var result = await utils.ReadFileWithMetadataAsync(_targetPath, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(result.Encoding.GetPreamble());
    }

    [Fact]
    public async Task ReadFileWithMetadataAsync_ShouldThrow_WhenCancellationRequested()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(_targetPath, new MockFileData("Line 1\nLine 2"));
        var utils = new FileUtils(fileSystem);

        using var cts = new CancellationTokenSource();

        // Сразу отменяем токен (CancelAsync — Sonar S6966: колбэки отмены асинхронные)
        await cts.CancelAsync();

        // Act & Assert
        // Any OperationCanceledException is a correct cancellation: the read now checks the token
        // up front, so the exception is thrown by ThrowIfCancellationRequested rather than by an
        // aborted Task. Asserting the exact TaskCanceledException pinned an implementation detail.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await utils.ReadFileWithMetadataAsync(_targetPath, cts.Token);
        });
    }

    [Fact]
    public async Task SaveFileAsync_ExistingFile_ReplacesContentAndLeavesNoTempFile()
    {
        // The write goes to a sibling temp file and is swapped in only when complete, so a crash
        // or a full disk cannot truncate the user's file. A successful write must not leave the
        // temp file behind either.
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [_targetPath] = new MockFileData("original content")
        });
        var utils = new FileUtils(fileSystem);

        await utils.SaveFileAsync(_targetPath, ["rewritten"], new UTF8Encoding(false), "\r\n", true);

        Assert.Equal("rewritten\r\n", await fileSystem.File.ReadAllTextAsync(_targetPath, TestContext.Current.CancellationToken));
        Assert.Equal([_targetPath], fileSystem.AllFiles);
    }

    [Fact]
    public async Task SaveFileAsync_NewFile_CreatesDirectoryAndLeavesNoTempFile()
    {
        var fileSystem = new MockFileSystem();
        var utils = new FileUtils(fileSystem);

        await utils.SaveFileAsync(_targetPath, ["one", "two"], new UTF8Encoding(false), "\n", false);

        Assert.Equal("one\ntwo", await fileSystem.File.ReadAllTextAsync(_targetPath, TestContext.Current.CancellationToken));
        Assert.Equal([_targetPath], fileSystem.AllFiles);
        Assert.True(fileSystem.Directory.Exists(_targetDirectory));
    }

    [Fact]
    public async Task SaveFileAsync_OverwritesExistingFileWithNewLineEnding_KeepsTargetFileOnly()
    {
        // Repeated writes (the normal edit loop) must stay idempotent: two saves in a row leave one
        // file with the last content, not a directory full of temp leftovers.
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [_targetPath] = new MockFileData("first")
        });
        var utils = new FileUtils(fileSystem);

        await utils.SaveFileAsync(_targetPath, ["second"], new UTF8Encoding(false), "\n", false);
        await utils.SaveFileAsync(_targetPath, ["third"], new UTF8Encoding(false), "\n", false);

        Assert.Equal("third", await fileSystem.File.ReadAllTextAsync(_targetPath, TestContext.Current.CancellationToken));
        Assert.Equal([_targetPath], fileSystem.AllFiles);
    }
}

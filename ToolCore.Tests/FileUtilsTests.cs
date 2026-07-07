using System.IO.Abstractions.TestingHelpers;
using System.Text;

namespace ToolCore.Tests;

public class FileUtilsTests
{
    private const string _targetPath = @"C:\data\file.txt";

    [Fact]
    public async Task ReadFileWithMetadataAsync_ShouldReturnEmpty_WhenFileDoesNotExist()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        var utils = new FileUtils(fileSystem);

        // Act
        var (lines, _, _, hasFinalNewLine) = await utils.ReadFileWithMetadataAsync(_targetPath, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(lines);
        Assert.False(hasFinalNewLine);
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
        var (readLines, _, readSeparator, readHasFinalNewLine) = await utils.ReadFileWithMetadataAsync(_targetPath, TestContext.Current.CancellationToken);

        // Assert 2: Проверяем идентичность метаданных
        Assert.Equal(sourceLines, readLines);
        Assert.Equal(originalSeparator, readSeparator);
        Assert.Equal(originalHasFinalNewLine, readHasFinalNewLine);
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
        var (_, readEncoding, _, _) = await utils.ReadFileWithMetadataAsync(_targetPath, TestContext.Current.CancellationToken);

        // Assert
        // Сравнение преамбул (массива байтов BOM) гарантирует точную идентификацию маркера
        Assert.Equal(encodingWithBom.GetPreamble(), readEncoding.GetPreamble());
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
        var (_, readEncoding, _, _) = await utils.ReadFileWithMetadataAsync(_targetPath, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(readEncoding.GetPreamble());
    }

    [Fact]
    public async Task ReadFileWithMetadataAsync_ShouldNormalizeMixedLineEndings_AndPickFirstOne()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        // Напрямую добавляем в эмулятор строку с «грязными» (смешанными) переносами
        var mixedText = "FirstLine\r\nSecondLine\nThirdLine";
        fileSystem.AddFile(_targetPath, new MockFileData(mixedText));

        var utils = new FileUtils(fileSystem);

        // Act
        var (lines, _, separator, hasFinalNewLine) = await utils.ReadFileWithMetadataAsync(_targetPath, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, lines.Count);
        Assert.Equal("FirstLine", lines[0]);
        Assert.Equal("SecondLine", lines[1]);
        Assert.Equal("ThirdLine", lines[2]);

        Assert.Equal("\r\n", separator); // Должен подхватиться первый встреченный разделитель
        Assert.False(hasFinalNewLine);   // Файл не оканчивается переносом
    }

    [Fact]
    public async Task ReadFileWithMetadataAsync_ShouldHandleSingleLineWithoutNewLine()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(_targetPath, new MockFileData("JustOneLine"));
        var utils = new FileUtils(fileSystem);

        // Act
        var (lines, _, _, hasFinalNewLine) = await utils.ReadFileWithMetadataAsync(_targetPath, TestContext.Current.CancellationToken);

        // Assert
        var singleLine = Assert.Single(lines);
        Assert.Equal("JustOneLine", singleLine);
        Assert.False(hasFinalNewLine);
    }

    [Fact]
    public async Task ReadFileWithMetadataAsync_ShouldThrow_WhenCancellationRequested()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(_targetPath, new MockFileData("Line 1\nLine 2"));
        var utils = new FileUtils(fileSystem);

        using var cts = new CancellationTokenSource();

        // Сразу отменяем токен
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
        {
            await utils.ReadFileWithMetadataAsync(_targetPath, cts.Token);
        });
    }
}

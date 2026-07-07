using System.IO.Abstractions;
using System.Text;

namespace ToolCore;

public class FileUtils
{
    private readonly IFileSystem _fileSystem;

    // Конструктор по умолчанию использует реальную ОС, а для тестов мы сможем передать мок
    public FileUtils(IFileSystem? fileSystem = null)
    {
        _fileSystem = fileSystem ?? new FileSystem();
    }

    public async Task<(List<string> Lines, Encoding Encoding, string Separator, bool HasFinalNewLine)> ReadFileWithMetadataAsync(
        string filepath, CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        Encoding encoding = new UTF8Encoding(false);
        var separator = Environment.NewLine;
        var hasFinalNewLine = false;

        if (!_fileSystem.File.Exists(filepath)) // Используем интерфейс
        {
            return (lines, encoding, separator, hasFinalNewLine);
        }

        // Ограничиваем максимальный размер
        var fileInfo = _fileSystem.FileInfo.New(filepath);
        const long maxAllowedSize = 2 * 1024 * 1024; // 2 МБ и то много
        if (fileInfo.Length > maxAllowedSize)
        {
            // Файл слишком большой для этого метода, возвращаем пустой результат
            return (lines, encoding, separator, hasFinalNewLine);
        }

        // ПРОВЕРКА НА БИНАРНОСТЬ: Заглядываем в начало файла
        using (var fsCheck = _fileSystem.FileStream.New(filepath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
        {
            var sampleBuffer = new byte[1024];
            var bytesRead = await fsCheck.ReadAsync(sampleBuffer, 0, sampleBuffer.Length, cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < bytesRead; i++)
            {
                if (sampleBuffer[i] == 0x00) // Обнаружен Null-символ -> это бинарный файл
                {
                    // Пропускаем чтение бинарного файла
                    return (lines, encoding, separator, hasFinalNewLine);
                }
            }
        }

        // Читаем текстовый файл
        string fullText;
        using (var fs = _fileSystem.FileStream.New(filepath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
        using (var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true))
        {
            fullText = await reader.ReadToEndAsync().ConfigureAwait(false);
            encoding = reader.CurrentEncoding;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(fullText))
        {
            return (lines, encoding, separator, hasFinalNewLine);
        }

        // Определение разделителей
        if (fullText.Contains("\r\n")) separator = "\r\n";
        else if (fullText.Contains("\n")) separator = "\n";
        else if (fullText.Contains("\r")) separator = "\r";

        if (fullText.EndsWith("\n") || fullText.EndsWith("\r"))
        {
            hasFinalNewLine = true;
        }

        // Парсинг строк
        var splitSeparators = new[] { "\r\n", "\r", "\n" };
        var allLines = fullText.Split(splitSeparators, StringSplitOptions.None);

        if (allLines.Length > 0)
        {
            lines.AddRange(allLines);

            if (hasFinalNewLine && lines.Count > 0 && string.IsNullOrEmpty(lines[lines.Count - 1]))
            {
                lines.RemoveAt(lines.Count - 1);
            }
        }

        return (lines, encoding, separator, hasFinalNewLine);
    }

    public async Task SaveFileAsync(string filepath, List<string> lines, Encoding encoding, string separator, bool hasFinalNewLine)
    {
        var content = string.Join(separator, lines);

        if (hasFinalNewLine)
        {
            content += separator;
        }

        var directory = _fileSystem.Path.GetDirectoryName(filepath);
        if (!string.IsNullOrEmpty(directory) && !_fileSystem.Directory.Exists(directory))
        {
            _fileSystem.Directory.CreateDirectory(directory);
        }

        // Создаем файл через фабрику интерфейса
        using var fs = _fileSystem.FileStream.New(filepath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        using var writer = new StreamWriter(fs, encoding);
        await writer.WriteAsync(content).ConfigureAwait(false);
    }
}

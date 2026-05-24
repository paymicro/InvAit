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

        // Открываем FileStream через фабрику интерфейса
        using var fs = _fileSystem.FileStream.New(filepath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        using var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true);

        reader.Peek();
        encoding = reader.CurrentEncoding;

        var sb = new StringBuilder();
        var buffer = new char[4096];
        int charsRead;
        var detectedSeparator = false;
        var lastCharWasNewLine = false;

        while ((charsRead = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var i = 0; i < charsRead; i++)
            {
                var c = buffer[i];

                if (c == '\r')
                {
                    var nextChar = '\0';
                    if (i + 1 < charsRead)
                    {
                        nextChar = buffer[i + 1];
                    }
                    else
                    {
                        var peeked = reader.Peek();
                        if (peeked != -1) nextChar = (char)peeked;
                    }

                    if (nextChar == '\n')
                    {
                        if (!detectedSeparator) { separator = "\r\n"; detectedSeparator = true; }
                        lines.Add(sb.ToString());
                        sb.Clear();
                        i++;
                        lastCharWasNewLine = true;
                    }
                    else
                    {
                        if (!detectedSeparator) { separator = "\r"; detectedSeparator = true; }
                        lines.Add(sb.ToString());
                        sb.Clear();
                        lastCharWasNewLine = true;
                    }
                }
                else if (c == '\n')
                {
                    if (!detectedSeparator) { separator = "\n"; detectedSeparator = true; }
                    lines.Add(sb.ToString());
                    sb.Clear();
                    lastCharWasNewLine = true;
                }
                else
                {
                    sb.Append(c);
                    lastCharWasNewLine = false;
                }
            }
        }

        if (sb.Length > 0)
        {
            lines.Add(sb.ToString());
            hasFinalNewLine = false;
        }
        else if (lines.Count > 0 && lastCharWasNewLine)
        {
            hasFinalNewLine = true;
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

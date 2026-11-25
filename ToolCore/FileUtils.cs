using System.Globalization;
using System.IO.Abstractions;
using System.Text;

namespace ToolCore;

/// <summary>
/// Why <see cref="FileUtils.ReadFileWithMetadataAsync"/> did or did not return the file's lines.
/// The reason belongs to the RESULT: an empty line list alone is indistinguishable from an empty
/// file, so a 3 MB file or a binary file used to come back "successfully" as zero lines and every
/// edit of it was reported as "oldStr not found" — a retry loop the model could not escape.
/// </summary>
public enum FileReadStatus
{
    /// <summary>Lines and metadata describe the file.</summary>
    Ok,

    /// <summary>The file does not exist.</summary>
    NotFound,

    /// <summary>Above the 2 MB edit limit — not read, see <see cref="FileReadResult.SkipReason"/>.</summary>
    TooLarge,

    /// <summary>
    /// Not readable as text, so nothing was decoded: a NUL byte in the first 1 KB of a BOM-less
    /// file, or bytes no available charset could decode (no BOM, not strict UTF-8, and no usable
    /// `.editorconfig` charset). See <see cref="FileReadResult.SkipReason"/> for which one.
    /// </summary>
    Binary
}

/// <summary>
/// What one read found. <see cref="Lines"/> is EMPTY for every non-<see cref="FileReadStatus.Ok"/>
/// status, and <see cref="SkipReason"/> says why in words the model can act on.
/// Plain settable properties, not `init`: netstandard2.0 has no IsExternalInit here.
/// </summary>
public sealed class FileReadResult
{
    public FileReadStatus Status { get; set; }

    public List<string> Lines { get; set; } = new List<string>();

    public Encoding Encoding { get; set; } = new UTF8Encoding(false);

    /// <summary>
    /// The dominant line break: the MOST FREQUENT kind, ties broken CRLF &gt; LF &gt; CR (the
    /// single algorithm in <see cref="TextFileCodec.DetectSeparator"/>, counted over the file's
    /// BYTES — the same answer the VS Code reader gives). Deliberately "\n" rather than
    /// <see cref="Environment.NewLine"/> when the file has none: the result must not depend on the
    /// machine the read ran on.
    /// </summary>
    public string Separator { get; set; } = "\n";

    public bool HasFinalNewLine { get; set; }

    /// <summary>More than one KIND of line break occurs in the text (CRLF / lone LF / lone CR).</summary>
    public bool HasMixedLineEndings { get; set; }

    /// <summary>Human-readable reason for a non-<see cref="FileReadStatus.Ok"/> status; null when the read succeeded.</summary>
    public string? SkipReason { get; set; }
}

public class FileUtils
{
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// The only other opinion about a file's encoding: when its bytes carry no BOM and are not
    /// valid UTF-8, `.editorconfig` may NAME the charset. Resolved through the same resolver
    /// `create_file` uses, so read and write can never disagree about the tree.
    /// </summary>
    private readonly EditorConfigResolver _editorConfig;

    // Конструктор по умолчанию использует реальную ОС, а для тестов мы сможем передать мок
    public FileUtils(IFileSystem? fileSystem = null)
    {
        _fileSystem = fileSystem ?? new FileSystem();
        _editorConfig = new EditorConfigResolver(_fileSystem);
    }

    /// <summary>
    /// Reads a text file together with the metadata needed to write it back unchanged
    /// (encoding, dominant separator, whether it ends with a line break).
    ///
    /// A file this method did NOT read says so: <see cref="FileReadStatus.TooLarge"/>,
    /// <see cref="FileReadStatus.Binary"/> and <see cref="FileReadStatus.NotFound"/> carry a
    /// <see cref="FileReadResult.SkipReason"/>. The old contract returned an empty line list, which
    /// the host could only interpret as "the file has no lines": it saw `search.Count >
    /// target.Count` and reported "oldStr not found" for EVERY edit.
    /// </summary>
    public async Task<FileReadResult> ReadFileWithMetadataAsync(string filepath, CancellationToken cancellationToken = default)
    {
        // Cancellation first: the answer is cheap, and it must not depend on how the stream
        // implementation treats an already-cancelled token.
        cancellationToken.ThrowIfCancellationRequested();

        var result = new FileReadResult();

        if (!_fileSystem.File.Exists(filepath)) // Используем интерфейс
        {
            result.Status = FileReadStatus.NotFound;
            result.SkipReason = "file doesn't exist";
            return result;
        }

        // Ограничиваем максимальный размер
        var fileInfo = _fileSystem.FileInfo.New(filepath);
        const long maxAllowedSize = 2 * 1024 * 1024; // 2 МБ и то много
        if (fileInfo.Length > maxAllowedSize)
        {
            // Файл слишком большой для построчной правки — говорим об этом вызывающему:
            // пустой список строк он прочитал бы как «файл пуст» и отчитался бы oldStr not found.
            result.Status = FileReadStatus.TooLarge;
            // InvariantCulture: сообщение читает МОДЕЛЬ — разделитель дробной части не должен
            // зависеть от локали машины (иначе "3,0 MB" на ru-RU).
            result.SkipReason = string.Format(
                CultureInfo.InvariantCulture,
                "file is {0:0.0} MB, above the 2 MB edit limit",
                fileInfo.Length / (1024.0 * 1024.0));
            return result;
        }

        // The file is read ONCE, as bytes: identification, the probes and the separator count all
        // work on the same buffer, and none of them can decode the file a second time with a
        // different answer. Bounded by the 2 MB edit limit checked above.
        byte[] bytes;
        using (var fs = _fileSystem.FileStream.New(filepath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
        using (var buffered = new MemoryStream())
        {
            var chunk = new byte[8192];
            int read;
            while ((read = await fs.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false)) > 0)
                await buffered.WriteAsync(chunk, 0, read, cancellationToken);
            bytes = buffered.ToArray();
        }

        // SAME identification as the writer: BOM first, and only then the probes.
        var identity = FileIdentity.FromBytes(bytes);
        var start = identity.Preamble.Length;

        if (!identity.HasBom)
        {
            // ПРОВЕРКА НА БИНАРНОСТЬ: NUL в первых 1 КБ — это не текст. Проверяется только файл
            // БЕЗ BOM: у UTF-16/UTF-32 старшие байты ASCII-символов тоже нули, и такой файл
            // объявлялся бы бинарным, хотя он опознан по преамбуле.
            var limit = Math.Min(bytes.Length, 1024);
            for (var i = 0; i < limit; i++)
            {
                if (bytes[i] == 0x00) // Обнаружен Null-символ -> это бинарный файл
                {
                    result.Status = FileReadStatus.Binary;
                    result.SkipReason = "file looks binary (NUL byte in the first 1 KB)";
                    return result;
                }
            }

            // No BOM and not valid UTF-8: decoding it as UTF-8 is how a cp1251/latin1 file became
            // U+FFFD and was then SAVED BACK as EF BF BD — the edit destroyed the file while
            // reporting success. The tree may name the charset; without a name the read refuses.
            if (!TextFileCodec.IsStrictUtf8(bytes, complete: true))
            {
                var settings = _editorConfig.Resolve(filepath);
                var declared = settings.CreateEncoding();
                if (declared == null)
                {
                    result.Status = FileReadStatus.Binary;
                    result.SkipReason = settings.Charset == null
                        ? "file has no BOM and is not valid UTF-8, and .editorconfig declares no charset. " +
                          "Add charset to .editorconfig, or edit the file with bash."
                        : $"file has no BOM and is not valid UTF-8, and .editorconfig declares charset '{settings.Charset}', which is not supported. " +
                          "Declare a supported charset, or edit the file with bash.";
                    return result;
                }

                // The bytes on disk stay as they are: the identity keeps the file's own (empty)
                // preamble and only takes the ENCODING from the tree.
                identity = FileIdentity.FromCharset(declared, declared, settings.Charset!, identity.Preamble);
                start = identity.Preamble.Length;
            }
        }

        string fullText;
        try
        {
            fullText = identity.Encoding.GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException e)
        {
            // A BOM already named the encoding and the body contradicts it. Nothing is decoded with
            // replacement characters: that is precisely what got rewritten into the file.
            result.Status = FileReadStatus.Binary;
            result.SkipReason = $"file is not valid {identity.CharsetName}: {e.Message}";
            return result;
        }

        cancellationToken.ThrowIfCancellationRequested();

        result.Status = FileReadStatus.Ok;
        result.SkipReason = null;
        // The SAVING encoding, not the body codec: SaveFileAsync writes through a StreamWriter,
        // which emits GetPreamble() — a `utf-8-bom` file must keep exactly ONE BOM after an edit.
        result.Encoding = identity.SavingEncoding;

        if (string.IsNullOrEmpty(fullText))
        {
            // Пустой файл: строк нет, разделитель остаётся "\n" по умолчанию
            return result;
        }

        // The ONE splitter: lines, final-newline flag and the line-break scan live in TextLines.
        var scan = TextLines.FromText(fullText);
        result.Lines.AddRange(scan.Lines);
        result.HasFinalNewLine = scan.HasFinalNewLine;

        // The separator is counted over the file's BYTES, not over the decoded text: the encoding
        // decides how wide a break is, and it must be the same algorithm the writer glues with.
        // Отсутствие переносов оставляет "\n" — Environment.NewLine сделал бы результат зависимым
        // от машины и сломал бы сравнение «файл не изменился».
        result.Separator = TextFileCodec.DetectSeparator(bytes, start, identity.Encoding) ?? "\n";

        // Смешанные переносы = в тексте больше ОДНОГО вида переноса. Однородный файл (даже с
        // одинокими CR) смешанным не считается.
        result.HasMixedLineEndings = TextFileCodec.HasMixedLineEndings(bytes, start, identity.Encoding);

        return result;
    }

    /// <summary>
    /// Writes the lines back with the given encoding and separator.
    ///
    /// ATOMIC: the content is written to a temp file in the SAME directory and only a COMPLETED
    /// temp file is swapped onto the target. A crash, a full disk or a killed host in the middle of
    /// the write can then not truncate the user's file — opening the target with FileMode.Create
    /// (the old behaviour) zeroes it before a single byte of the new content exists.
    /// The temp file lives in the target's directory on purpose: replace/move only work on one volume.
    /// </summary>
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

        // Уникальный хвост: две параллельные записи одного файла не должны затирать temp друг друга
        var tempPath = filepath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using (var fs = _fileSystem.FileStream.New(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            using (var writer = new StreamWriter(fs, encoding))
            {
                await writer.WriteAsync(content).ConfigureAwait(false);
            }

            // Replace переносит содержимое на существующий файл, сохраняя его ACL/время создания;
            // Move — для нового файла (Replace требует существующего назначения).
            if (_fileSystem.File.Exists(filepath))
                _fileSystem.File.Replace(tempPath, filepath, null);
            else
                _fileSystem.File.Move(tempPath, filepath);
        }
        catch
        {
            // Наполовину записанный temp не должен остаться рядом с файлом пользователя
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>Best-effort temp cleanup: a failing delete must not mask the original write error.</summary>
    private void TryDelete(string path)
    {
        try
        {
            if (_fileSystem.File.Exists(path))
                _fileSystem.File.Delete(path);
        }
        catch (IOException)
        {
            // ignore — cleanup only
        }
        catch (UnauthorizedAccessException)
        {
            // ignore — cleanup only
        }
    }
}

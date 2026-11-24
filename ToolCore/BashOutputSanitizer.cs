using System.Text;
using System.Text.RegularExpressions;

namespace ToolCore;

/// <summary>
/// Убирает из stdout/stderr bash служебные упоминания временного файла скрипта.
/// <para>
/// <c>sh</c> подставляет имя файла в каждое диагностическое сообщение:
/// <c>C:\Users\pc\AppData\Local\Temp\invait_8f45....sh: line 1: strings: command not found</c>.
/// Модель не может этим путём воспользоваться — файл удаляется в <c>finally</c> сразу после
/// запуска, — а строка съедает контекст и выглядит как утечка внутренней кухни. Поэтому путь
/// вырезается, а полезная часть сообщения остаётся байт в байт:
/// <c>line 1: strings: command not found</c>.
/// </para>
/// <para>
/// Правило вырезания — по ЛИЧНОСТИ, а не по форме: удаляется НАШ временный файл (точный путь,
/// который мы сами передали в <c>sh</c>, и имя вида <c>invait_&lt;32 hex&gt;.sh</c>), но никогда
/// «любой .sh». Иначе ошибка в собственном скрипте пользователя (<c>build.sh: line 3: ...</c>)
/// потеряла бы имя файла — то есть санитайзер уничтожал бы ровно ту информацию, ради которой
/// сообщение и печатается.
/// </para>
/// <para>
/// Зеркало в Node-хосте: <c>VsCode/src/toolHandlers/bashOutput.ts</c>. Хосты не делят код, поэтому
/// правило и форма имени файла дублируются там с обратной ссылкой сюда — правка одного без
/// другого вернёт баг в один из каналов.
/// </para>
/// </summary>
public static class BashOutputSanitizer
{
    /// <summary>
    /// Имя временного файла, которое создаёт <see cref="ProcessExecutor.ExecuteBashAsync"/>.
    /// Форма имени — часть контракта: по ней санитайзер узнаёт свой файл, когда оболочка
    /// переписала путь (например, в <c>/c/Users/...</c> или через прямые слэши).
    /// <para>
    /// Класс символов включает <c>:</c> и разделители пути, чтобы матч начинался с буквы диска
    /// (<c>C:\...</c>), а не с первого каталога: иначе в выводе остался бы огрызок <c>C:\</c>.
    /// Хвостовой разделитель <c>:</c> съедается вместе с путём — он принадлежит формату
    /// <c>файл: сообщение</c>, а не сообщению. <c>[ \t]?</c> вместо <c>\s?</c> намеренно:
    /// <c>\s</c> захватил бы перевод строки и склеил бы соседние строки вывода.
    /// </para>
    /// <para>
    /// Префикс пути обязан заканчиваться разделителем, а сам матч — начинаться на границе
    /// (<c>(?&lt;!…)</c>). Без обоих условий шаблон срабатывал бы на файле пользователя
    /// <c>myinvait_abc.sh</c>: хвост <c>my</c> попадал в <c>[\w.:/\\-]*</c>, и имя файла
    /// терялось — то есть санитайзер уничтожал бы ровно ту информацию, которую должен беречь.
    /// </para>
    /// <para>
    /// Хвост имени (<c>[\w-]+</c>) намеренно не фиксирует формат GUID: Node-хост называет файл
    /// <c>invait_&lt;ms&gt;_&lt;rand&gt;.sh</c>, C# — <c>invait_&lt;32 hex&gt;.sh</c>, а правило
    /// вырезания должно быть одно на оба канала.
    /// </para>
    /// <para>
    /// Принятая цена: файл пользователя, названный <c>*invait_*.sh</c>, тоже потеряет имя. Префикс
    /// <c>invait_</c> — наш зарезервированный маркер, поэтому такой файл считается нашим.
    /// </para>
    /// </summary>
    private static readonly Regex TempScriptNamePattern = new(
        @"(?<![\w.:/\\-])['""]?(?:[\w.:/\\-]*[/\\])?invait_[\w-]+\.sh['""]?(:[ \t]?)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Вырезает из вывода упоминания временного файла скрипта.
    /// </summary>
    /// <param name="output">stdout или stderr процесса.</param>
    /// <param name="tempScriptPath">
    /// Путь, переданный в <c>sh</c>. Нужен для случая, когда в пути есть пробелы (имя каталога
    /// вида <c>John Doe</c>): шаблон по имени файла такой путь целиком не покрывает.
    /// </param>
    public static string Sanitize(string? output, string? tempScriptPath)
    {
        if (string.IsNullOrEmpty(output))
            return string.Empty;

        var original = output!;
        var cleaned = ReplaceExactPath(original, tempScriptPath);
        cleaned = TempScriptNamePattern.Replace(cleaned, string.Empty);

        return DropEmptiedLines(original, cleaned);
    }

    /// <summary>
    /// Удаляет точный путь, который мы передали в <c>sh</c>, и его вариант с прямыми слэшами.
    /// </summary>
    private static string ReplaceExactPath(string output, string? tempScriptPath)
    {
        if (string.IsNullOrEmpty(tempScriptPath))
            return output;

        var variants = new List<string> { tempScriptPath! };
        var forwardSlash = tempScriptPath!.Replace('\\', '/');
        if (!string.Equals(forwardSlash, tempScriptPath, StringComparison.Ordinal))
            variants.Add(forwardSlash);

        // Длинные варианты первыми: иначе короткий съел бы часть длинного.
        variants.Sort((a, b) => b.Length.CompareTo(a.Length));

        var pattern = $@"['""]?(?:{string.Join("|", variants.Select(Regex.Escape))})['""]?(:[ \t]?)?";

        // IgnoreCase: Windows-пути регистронезависимы, а оболочка может сменить регистр буквы диска.
        return Regex.Replace(output, pattern, string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Убирает строки, которые стали пустыми ИЗ-ЗА вырезания пути (например, вывод <c>echo $0</c>).
    /// Строки, пустые изначально, остаются: это часть вывода команды, а не наш мусор.
    /// </summary>
    private static string DropEmptiedLines(string original, string cleaned)
    {
        if (original.Length == cleaned.Length)
            return cleaned;

        var originalLines = original.Split('\n');
        var cleanedLines = cleaned.Split('\n');
        if (originalLines.Length != cleanedLines.Length)
            return cleaned;

        var builder = new StringBuilder(cleaned.Length);
        var first = true;
        for (var i = 0; i < cleanedLines.Length; i++)
        {
            if (cleanedLines[i].Trim().Length == 0 && originalLines[i].Trim().Length > 0)
                continue;

            if (!first)
                builder.Append('\n');

            builder.Append(cleanedLines[i]);
            first = false;
        }

        return builder.ToString();
    }
}

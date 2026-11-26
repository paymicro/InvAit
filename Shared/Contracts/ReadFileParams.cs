using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Shared.Contracts;

public class ReadFileParams
{
    [Description("Path to file. Absolute or relative. Unique in list")]
    [JsonPropertyName("path")]
    public string Path { get; set; } = null!;

    // Optionality выражена ТИПОМ (`int?` → union ["integer","null"] в strict-схеме), а НЕ
    // [DefaultValue]: так модель, которой нечего сказать про диапазон, присылает null вместо
    // выдуманного сентинела, и обязательность КЛЮЧА при этом сохраняется — strict-режим требует
    // присутствия поля, но не осмысленного значения в нём.
    //
    // Почему [DefaultValue] нельзя — с обеих сторон от nullable-типа:
    //  - при `int` он делал свойство optional для SchemaProcessor, тот заворачивал тип в
    //    ["integer","null"], и модель получала право прислать `"startLine": null`. STJ на
    //    non-nullable int бросает, JsonUtils.GetObject глотал исключение и отдавал null, а хост
    //    падал NullReferenceException'ом на `.Count`. То есть багом было не «optional», а
    //    nullable-ОБЕЩАНИЕ схемы при non-nullable ТИПЕ;
    //  - при `int?` он не добавляет к схеме ничего (nullable-тип уже делает поле optional) и лишь
    //    дописывает модели «(default '-1')» — второй источник правды рядом с текстом, ровно та
    //    ошибка, которую вычистили из DiffEdit.
    //
    // Семантика: null == -1 == 0 == «от начала файла / без ограничения»; НЕположительное значение
    // означает отсутствие диапазона — поэтому 2000 строк — это лимит УМОЛЧАНИЯ, а явно
    // присланное число исполняется как есть (описание поля говорит об этом прямо: обещание
    // «Maximum: 2000» код не выполнял, и модель верила описанию).
    // Нормализация null/0/-1 → ОДНО число и дедуп живут в ToolCore/ReadFilesPayload.TryParse,
    // в ОДНОМ месте на оба хоста (VS Code повторяет его в fileOps.ts): контракт не диктует хостам
    // форму значения, а второй источник истины здесь уже был багом.
    [Description("Start line, 1-based. lineCount counts FROM this line. null (or -1) = read from the top of the file")]
    [JsonPropertyName("startLine")]
    public int? StartLine { get; set; }

    // Про 300 KB сказано потому, что обещать «5000 строк вернут 5000» и молчать про обрезку
    // результата — хуже, чем сказать оба числа: ToolResult режет ответ с НАЧАЛА (MessageContent),
    // и модель получала хвост, не понимая, куда делись первые строки.
    [Description("How many lines to return starting at startLine. null (or -1) = to the end of the file, capped at 2000 lines (the total is then reported). An explicit count is honoured as sent, up to the 300 KB tool-result limit, which drops the beginning of very long output")]
    [JsonPropertyName("lineCount")]
    public int? LineCount { get; set; }
}

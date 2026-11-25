using System.ComponentModel;

namespace Shared.Contracts;

public class DiffEdit
{
    /// <summary>
    /// Начальная линия (с tolerance)
    /// </summary>
    [Description("1-based line where oldStr is expected, or null. Only matches within +/-5 lines of it are considered; if nothing matches there the hint is IGNORED and the whole file is searched again (null = search the whole file).")]
    public int? ApproximateLine { get; set; } = null;

    [Description("Unique fragment of code. An empty or whitespace-only value is NOT an insertion point: it addresses one BLANK line near approximateLine (nearest within +/-5 lines, else the first blank line in the file) and is always reported as applied.")]
    public string OldStr { get; set; } = string.Empty;

    [Description("New fragment of code")]
    public string NewStr { get; set; } = string.Empty;

    /// <summary>
    /// Заменить ВСЕ вхождения. По умолчанию false: если oldStr встречается несколько раз
    /// и approximateLine не выделяет одно из них, правка НЕ применяется и в отчёте
    /// приходит число вхождений с номерами линий.
    /// <para>
    /// Тип именно <c>bool?</c>, а не <c>bool</c>: схема инструмента объявляет поле
    /// nullable-union, то есть модель ВПРАВЕ прислать <c>"replaceAll": null</c>. Nullable-тип
    /// и есть источник этой опциональности — <c>IsNullableProperty</c> возвращает true для
    /// <c>bool?</c> без всяких атрибутов. На non-nullable bool System.Text.Json бросал
    /// исключение, JsonUtils.GetObject его глотал и возвращал null — и весь батч правок
    /// падал с сообщением «Failed to get replacements» без файла и без причины.
    /// Семантика: null == false.
    /// </para>
    /// <para>
    /// Атрибут <c>[DefaultValue(false)]</c> здесь не нужен и удалён: к схеме он ничего не
    /// добавлял (опциональность уже выражена nullable-типом), а только дописывал в описание
    /// модели «(default 'False')» — второй источник правды рядом с текстом «Default: false».
    /// </para>
    /// </summary>
    [Description("Replace every occurrence instead of exactly one. Default: false — a search found in several places is reported as ambiguous and NOT applied. Has no effect when oldStr is empty or whitespace-only: that block always settles on a single blank line.")]
    public bool? ReplaceAll { get; set; }
}

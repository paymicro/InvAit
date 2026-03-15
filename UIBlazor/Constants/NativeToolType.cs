namespace UIBlazor.Constants;

public static class NativeToolType
{
    /// <summary>
    /// Строка она и есть строка
    /// </summary>
    public const string String = "string";

    /// <summary>
    /// Число с плавающей точкой
    /// </summary>
    public const string Number = "number";

    /// <summary>
    /// Целое число
    /// </summary>
    public const string Integer = "integer";

    /// <summary>
    ///<see cref="bool"/>
    /// </summary>
    public const string Boolean = "boolean";

    /// <summary>
    /// Массив
    /// </summary>
    public const string Array = "array";

    /// <summary>
    /// Вложенный объект
    /// </summary>
    public const string Object = "object";

    /// <summary>
    /// Null тип для union типов в Strict Mode
    /// </summary>
    public const string Null = "null";
}

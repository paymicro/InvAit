namespace UIBlazor.Models;

/// <summary>
/// Базовый токен ввода
/// </summary>
public abstract class InputToken
{
    public abstract string GetDisplayText();
}

/// <summary>
/// Текстовый токен
/// </summary>
public class TextToken : InputToken
{
    public string Text { get; set; } = string.Empty;

    public override string GetDisplayText() => Text;
}

/// <summary>
/// Токен файла
/// </summary>
public class FileToken : InputToken
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string? FileContent { get; set; }

    public override string GetDisplayText() => $"@{FileName}";
}

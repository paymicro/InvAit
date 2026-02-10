namespace UIBlazor.Options;

public class CommonOptions : BaseOptions
{
    public int ToolTimeoutMs { get; set => SetIfChanged(ref field, value); } = 3_000;

    public bool SendCurrentFile { get; set => SetIfChanged(ref field, value); } = true;

    public bool SendSolutionsStricture { get; set => SetIfChanged(ref field, value); } = true;

    // TODO
    public bool IsDarkTheme { get; set => SetIfChanged(ref field, value); } = true;
}

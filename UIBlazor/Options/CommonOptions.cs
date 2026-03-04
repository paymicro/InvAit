using System.Globalization;

namespace UIBlazor.Options;

public class CommonOptions : BaseOptions
{
    public int ToolTimeoutMs { get; set => SetIfChanged(ref field, value); } = 3_000;

    public bool SendCurrentFile { get; set => SetIfChanged(ref field, value); } = true;

    public bool SendSolutionsStricture { get; set => SetIfChanged(ref field, value); } = true;

    public string Culture { get; set => SetIfChanged(ref field, value); } = CultureInfo.CurrentCulture.Name;

    public int MaxRetries { get; set => SetIfChanged(ref field, value); } = 10;
}

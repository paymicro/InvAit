namespace UIBlazor.Options;

public class CommonOptions : BaseOptions
{
    public int ToolTimeoutMs { get; set => SetIfChanged(ref field, value); } = 3_000;
}

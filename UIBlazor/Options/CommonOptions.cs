namespace UIBlazor.Options;

public class CommonOptions : BaseOptions
{
    public int ToolTimeoutMs { get => field; set => SetIfChanged(ref field, value); } = 30_000;
}

namespace UIBlazor.Services;

public class VsCodeContextService : IVsCodeContextService
{
    public VsCodeContext? CurrentContext { get; private set; }

    /// <inheritdoc />
    public string? IdeType { get; private set; }

    public event Action? OnContextChanged;

    public void UpdateContext(VsCodeContext context)
    {
        CurrentContext = context;
        IdeType = context.IdeType;
        OnContextChanged?.Invoke();
    }
}
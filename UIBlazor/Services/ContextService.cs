namespace UIBlazor.Services;

public class ContextService : IContextService
{
    public VsContext? CurrentContext { get; private set; }

    /// <inheritdoc />
    public string? IdeType { get; private set; }

    public event Action? OnContextChanged;

    public void UpdateContext(VsContext context)
    {
        CurrentContext = context;
        IdeType = context.IdeType;
        OnContextChanged?.Invoke();
    }
}
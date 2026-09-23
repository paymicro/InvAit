namespace UIBlazor.Services.Interfaces
{
    public interface IContextService
    {
        VsContext? CurrentContext { get; }

        /// <summary>
        /// Cached IDE type from context (e.g. "vs", "vscode").
        /// Null if not yet received.
        /// </summary>
        string? IdeType { get; }

        event Action? OnContextChanged;

        void UpdateContext(VsContext context);
    }
}

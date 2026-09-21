namespace UIBlazor.Services.Interfaces
{
    public interface IVsCodeContextService
    {
        VsCodeContext? CurrentContext { get; }

        /// <summary>
        /// Cached IDE type from context (e.g. "vs", "vscode").
        /// Null if not yet received.
        /// </summary>
        string? IdeType { get; }

        event Action? OnContextChanged;

        void UpdateContext(VsCodeContext context);
    }
}

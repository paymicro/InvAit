namespace UIBlazor.Services
{
    public interface IVsCodeContextService
    {
        VsCodeContext? CurrentContext { get; }

        event Action? OnContextChanged;

        void UpdateContext(VsCodeContext context);
    }
}

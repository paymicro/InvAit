using Shared.Contracts;

namespace UIBlazor.Services
{
    public class VsCodeContextService : IVsCodeContextService
    {
        public VsCodeContext? CurrentContext { get; private set; }

        public event Action? OnContextChanged;

        public void UpdateContext(VsCodeContext context)
        {
            CurrentContext = context;
            OnContextChanged?.Invoke();
        }
    }
}

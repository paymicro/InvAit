namespace Shared.Contracts;

public interface IUiBridge
{
    Task OnVsResponseAsync(VsResponse response);
    Task OnErrorAsync(string message);
}

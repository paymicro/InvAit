namespace UIBlazor.Agents;

public interface IInternalExecutor
{
    Task<VsToolResult> ExecuteToolAsync(string name, string args, CancellationToken cancellationToken);
}

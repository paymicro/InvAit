namespace UIBlazor.Agents;

public interface IInternalExecutor
{
    Task<VsToolResult> ExecuteToolAsync(string name, IReadOnlyDictionary<string, object> args, CancellationToken cancellationToken);
}

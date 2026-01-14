using UIBlazor.Agents;

namespace UIBlazor.VS;

public interface IVsBridge
{
    //Task<VsToolResult> ReadFilesAsync(IReadOnlyDictionary<string, object> args);

    //Task<VsToolResult> ReadOpenFileAsync();

    //Task<VsToolResult> CreateFileAsync(IReadOnlyDictionary<string, object> args);

    //Task<VsToolResult> ExecAsync(IReadOnlyDictionary<string, object> args);

    //Task<VsToolResult> SearchFilesAsync(IReadOnlyDictionary<string, object> args);

    //Task<VsToolResult> GrepSearchAsync(IReadOnlyDictionary<string, object> args);

    //Task<VsToolResult> LsAsync();

    //Task<VsToolResult> FetchUrlAsync(IReadOnlyDictionary<string, object> args);

    //Task<VsToolResult> ApplyDiffAsync(IReadOnlyDictionary<string, object> args);

    //Task<VsToolResult> BuildSolutionAsync(string action);

    //Task<VsToolResult> GetErrorsAsync();

    Task<VsToolResult> ExecuteToolAsync(string name, IReadOnlyDictionary<string, object>? args = null);
}

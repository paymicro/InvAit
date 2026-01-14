using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.Build.Framework;
using Shared.Contracts;
using Shell = Microsoft.VisualStudio.Shell;
using Toolkit = Community.VisualStudio.Toolkit;
using VS = Community.VisualStudio.Toolkit.VS;
using Process = System.Diagnostics.Process;
using InvGen.Utils;

namespace InvGen.Agent;

public class BuiltInAgent
{
    public async Task<VsResponse> ExecuteAsync(VsRequest vsRequest)
    {
        var response = vsRequest.Action switch
        {
            BuiltInToolEnum.ReadOpenFile => await ReadCurrentlyOpenFileAsync(),
            BuiltInToolEnum.ReadFiles => await ReadFileAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
            //"insertTextAtCursor" => await HandleInsertTextAsync(request),
            _ => new VsResponse { Success = false, Error = "Unknown action" }
        };

        response.CorrelationId = vsRequest.CorrelationId;

        return response;
    }

    private async Task<VsResponse> ReadCurrentlyOpenFileAsync()
    {
        var docView = await VS.Documents.GetActiveDocumentViewAsync();
        if (docView?.Document?.FilePath == null)
            return new VsResponse
            {
                Success = false,
                Error = "No active document"
            };

        return await ReadFileAsync(new Dictionary<string, object>
        {
            { "files", JsonUtils.Serialize(new List<string> { docView.Document.FilePath }) }
        });
    }

    private async Task<VsResponse> ReadFileAsync(IReadOnlyDictionary<string, object> args)
    {
        var solutionPath = await GetSolutionPathAsync();
        var filesInString = args.GetString("files");
        var files = JsonUtils.Deserialize<List<string>>(filesInString);

        await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var stingBuilder = new StringBuilder();
        var isSuccess = true;
        foreach (var fileName in files)
        {
            var filepath = GetAbsolutePath(fileName, solutionPath);
            if (!File.Exists(filepath))
            {
                stingBuilder.AppendLine($"File \"{fileName}\" doesn't exist.");
                break;
            }

            stingBuilder.AppendLine($"\"{fileName}\" content");

            try
            {
                var lineNum = 0;
                foreach (var readLine in File.ReadLines(filepath))
                {
                    stingBuilder.AppendLine($"{++lineNum} | {readLine}");
                }
            }
            catch (Exception e)
            {
                Logger.Log(e.Message);
                stingBuilder.AppendLine($"Error {e.Message}");
                isSuccess = false;
            }
        }

        return new VsResponse
        {
            Success = isSuccess,
            Payload = stingBuilder.ToString()
        };
    }

    private async Task<string> GetSolutionPathAsync()
    {
        var solution = await VS.Solutions.GetCurrentSolutionAsync();
        return solution != null ? Path.GetDirectoryName(solution.FullPath) : Directory.GetCurrentDirectory();
    }

    private string GetAbsolutePath(string relativePath, string solutionPath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            throw new ArgumentException("Path cannot be null or empty.");
        }

        if (Path.IsPathRooted(relativePath))
        {
            return relativePath;
        }

        var path = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimStart('.', Path.DirectorySeparatorChar);
        return path.StartsWith(solutionPath) ? path : $"{solutionPath}{Path.DirectorySeparatorChar}{path}";
    }
}

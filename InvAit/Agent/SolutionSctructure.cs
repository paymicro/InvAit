using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Toolkit = Community.VisualStudio.Toolkit;
using VS = Community.VisualStudio.Toolkit.VS;

namespace InvAit.Agent;

public class SolutionSctructure
{
    // TODO: сделать настраиваемым?
    private const int _maxFilesInFolder = 25;

    public static async Task<string> GetSolutionPathAsync()
    {
        var solution = await VS.Solutions.GetCurrentSolutionAsync();
        return solution != null ? Path.GetDirectoryName(solution.FullPath) : Directory.GetCurrentDirectory();
    }

    private static string MakeRelativeToSolution(bool makeRelative, string fullPath, string solutionPath)
    {
        return !makeRelative || !fullPath.StartsWith(solutionPath, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.Substring(solutionPath.Length + 1);
    }

    private static async Task WalkSolutionItemsAsync(IEnumerable<Toolkit.SolutionItem> items, List<string> result, int indent, string solutionPath, bool makeRelative)
    {
        var indentString = new string(' ', indent * 2);
        var fileIndex = 0;
        var filesSkipped = 0;
        foreach (var item in items)
        {
            if (item.Type == Toolkit.SolutionItemType.PhysicalFile && !item.IsNonVisibleItem)
            {
                var ext = Path.GetExtension(item.FullPath).ToLower();
                if (ext is ".zip" or ".bin" or ".dll" or ".exe" or ".png" or ".jpg" or ".obj" or ".pdb")
                    continue;

                if (fileIndex++ < _maxFilesInFolder)
                {
                    result.Add($"{indentString}📄 {MakeRelativeToSolution(makeRelative, item.Name, solutionPath)}");
                }
                else
                {
                    filesSkipped++;
                }
            }
            else if (item.Type == Toolkit.SolutionItemType.PhysicalFolder || item.Type == Toolkit.SolutionItemType.Project)
            {
                var pathSegments = item.FullPath.Split(Path.DirectorySeparatorChar);
                if (pathSegments.Contains("bin") || pathSegments.Contains("obj"))
                    continue;

                result.Add($"{indentString}📁 {MakeRelativeToSolution(makeRelative, item.Name, solutionPath)}");
                await WalkSolutionItemsAsync(item.Children, result, indent + 1, solutionPath, makeRelative);
            }
        }

        if (filesSkipped > 0)
        {
            result.Add($"{indentString}... and {filesSkipped} more files ...");
        }
    }

    public static async Task<List<string>> BuildStructureAsync(bool makeRelative = false)
    {
        var result = new List<string>();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var projects = await VS.Solutions.GetAllProjectsAsync();
        var solutionPath = await GetSolutionPathAsync();

        foreach (var project in projects)
        {
            result.Add($"Project: {project.Name} | {project.FullPath}");
            await WalkSolutionItemsAsync(project.Children, result, 1, solutionPath, makeRelative);
        }

        return result;
    }
}

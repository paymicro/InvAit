using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Shared.Contracts;
using Toolkit = Community.VisualStudio.Toolkit;
using VS = Community.VisualStudio.Toolkit.VS;

namespace InvAit.Agent;

public class SolutionSctructure
{
    // TODO: сделать настраиваемым?
    private const int _maxFilesInFolder = 20;

    public static async Task<string> GetSolutionPathAsync()
    {
        var solution = await VS.Solutions.GetCurrentSolutionAsync();
        return solution != null ? Path.GetDirectoryName(solution.FullPath) : Directory.GetCurrentDirectory();
    }

    private static async Task WalkSolutionItemsAsync(IEnumerable<Toolkit.SolutionItem> items, List<string> result, int indent, string solutionPath, bool fullPath)
    {
        var indentString = new string(' ', indent * 2);

        var groupItems = items.GroupBy(i => i.Type); // группируем по типу

        // Сначала файлы
        var files = groupItems.FirstOrDefault(g => g.Key is Toolkit.SolutionItemType.PhysicalFile);
        if (files != null)
        {
            var fileIndex = 0;
            var filesSkipped = 0;

            foreach (var item in files)
            {
                if (item.IsNonVisibleItem)
                    continue;

                var ext = Path.GetExtension(item.FullPath).ToLower();
                if (ext is ".zip" or ".bin" or ".dll" or ".exe" or ".png" or ".jpg" or ".obj" or ".pdb")
                    continue;

                if (fileIndex++ < _maxFilesInFolder)
                {
                    result.Add($"{indentString}{VsCodeContext.FilePrefix} {(fullPath ? item.FullPath : item.Text)}");
                }
                else
                {
                    filesSkipped++;
                }
            }

            if (filesSkipped > 0)
            {
                result.Add($"{indentString}... and {filesSkipped} more files ...");
            }
        }

        // Папки
        var projects = groupItems.FirstOrDefault(g => g.Key is Toolkit.SolutionItemType.PhysicalFolder or Toolkit.SolutionItemType.Project);
        if (projects != null)
        {
            foreach (var item in projects)
            {
                if (item.Text is "bin" or "obj")
                    continue;

                result.Add($"{indentString}{VsCodeContext.DirPrefix} {item.FullPath}");
                await WalkSolutionItemsAsync(item.Children, result, indent + 1, solutionPath, fullPath);
            }
        }
    }

    public static async Task<List<string>> BuildStructureAsync(bool fullPath = true)
    {
        var result = new List<string>();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var projects = await VS.Solutions.GetAllProjectsAsync();
        var solutionPath = await GetSolutionPathAsync();

        result.Add($"Solution path: {VsCodeContext.DirPrefix} {solutionPath}");
        foreach (var project in projects)
        {
            result.Add($"Project: {project.Name} | {project.FullPath}");
            if (project.IsLoaded)
            {
                await WalkSolutionItemsAsync(project.Children, result, 1, solutionPath, fullPath);
            }
        }

        return result;
    }
}

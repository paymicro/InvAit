using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Toolkit = Community.VisualStudio.Toolkit;
using VS = Community.VisualStudio.Toolkit.VS;

namespace InvAit.Agent;

public class SolutionStructure
{
    // TODO: сделать настраиваемым?
    private const int _maxFilesInFolder = 25;

    public static async Task<string> GetSolutionPathAsync()
    {
        var solution = await VS.Solutions.GetCurrentSolutionAsync();
        return solution != null ? Path.GetDirectoryName(solution.FullPath) : Directory.GetCurrentDirectory();
    }

    private static async Task WalkSolutionItemsAsync(IEnumerable<Toolkit.SolutionItem> items, List<string> result, string solutionPath)
    {
        // Сначала файлы
        var files = items.Where(i => i.Type is Toolkit.SolutionItemType.PhysicalFile);
        if (files != null)
        {
            var fileIndex = 0;

            foreach (var item in files)
            {
                if (item.IsNonVisibleItem)
                    continue;

                var ext = Path.GetExtension(item.Text).ToLower();
                if (ext is ".zip" or ".bin" or ".dll" or ".exe" or ".png" or ".jpg" or ".obj" or ".pdb")
                    continue;

                if (fileIndex++ < _maxFilesInFolder)
                {
                    result.Add(item.FullPath);

                    // в файлах могут быть вложенные файлы
                    // xaml => xaml.cs
                    // razor => razor.cs + razor.css
                    await WalkSolutionItemsAsync(item.Children, result, solutionPath);
                }
            }

            // Note: skipped count no longer added as a string — it would pollute the raw path list
        }

        // Папки
        var projects = items.Where(i => i.Type is Toolkit.SolutionItemType.PhysicalFolder or Toolkit.SolutionItemType.Project);
        if (projects != null)
        {
            foreach (var item in projects)
            {
                if (item.Text is "bin" or "obj" or "node_modules" or "out" or "TestResults")
                    continue;

                await WalkSolutionItemsAsync(item.Children, result, solutionPath);
            }
        }
    }

    /// <summary>
    /// Builds a list of raw file paths in the solution (no formatting/emojis).
    /// Formatting is done on the UIBlazor side.
    /// </summary>
    public static async Task<List<string>> BuildStructureAsync()
    {
        var result = new List<string>();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var projects = await VS.Solutions.GetAllProjectsAsync(Toolkit.ProjectStateFilter.Loaded);

        var solutionPath = await GetSolutionPathAsync();

        foreach (var project in projects)
        {
            await WalkSolutionItemsAsync(project.Children, result, solutionPath);
        }

        return result;
    }

    /// <summary>
    /// Returns the list of project file paths (e.g. .csproj) in the solution.
    /// </summary>
    public static async Task<List<string>> GetProjectPathsAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var projects = await VS.Solutions.GetAllProjectsAsync(Toolkit.ProjectStateFilter.Loaded);
        return [.. projects.Select(p => p.FullPath)];
    }
}

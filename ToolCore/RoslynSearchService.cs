using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Shared.Contracts;

namespace ToolCore;

public class RoslynSearchService
{
    public async Task<VsResponse> FindReferencesAsync(object globalServiceProvider, string symbolName)
    {
        // Корректный уход на фоновый поток в .NET
        await Task.Yield();

        try
        {
            // Инициализируем компоненты Roslyn внутри метода
            var sp = (IServiceProvider)globalServiceProvider;
            var componentModel = (IComponentModel)sp.GetService(typeof(SComponentModel));
            if (componentModel == null)
            {
                return new VsResponse { Success = false, Error = "ComponentModel service not found." };
            }

            var workspace = componentModel.GetService<VisualStudioWorkspace>();
            if (workspace == null)
            {
                return new VsResponse { Success = false, Error = "VisualStudioWorkspace not found." };
            }

            var solution = workspace.CurrentSolution;

            // Находим декларации символов по текстовому имени во всем решении
            var symbols = (await SymbolFinder.FindSourceDeclarationsWithPatternAsync(
                solution,
                symbolName,
                SymbolFilter.Type | SymbolFilter.Member)).ToList();

            if (symbols.Count == 0)
            {
                return new VsResponse { Success = false, Error = $"Symbol '{symbolName}' isn't found." };
            }

            var sb = new StringBuilder();

            // Для каждого найденного символа
            foreach (var symbol in symbols)
            {
                // Вызываем поиск зависимостей
                var references = await SymbolFinder.FindReferencesAsync(symbol, solution);
                var refs = new Dictionary<string, HashSet<int>>();

                foreach (var reference in references)
                {
                    foreach (var location in reference.Locations)
                    {
                        if (location.Document == null)
                            continue;

                        if (!refs.TryGetValue(location.Document.FilePath, out var lines))
                        {
                            refs[location.Document.FilePath] = lines = new HashSet<int>();
                        }

                        var linePos = location.Location.GetLineSpan().StartLinePosition.Line + 1;
                        lines.Add(linePos);
                    }
                }

                if (refs.Count == 0)
                {
                    continue;
                }

                sb.AppendLine($"--- References for: {symbol.ToDisplayString()} ---");
                foreach (var r in refs)
                {
                    var sortedLines = r.Value.OrderBy(n => n);
                    sb.AppendLine($"Used in: {r.Key}, line: {string.Join(", ", sortedLines)}");
                }
            }

            return new VsResponse { Payload = sb.ToString() };
        }
        catch (Exception ex)
        {
            // Если Roslyn упадет из-за версий сборок, мы перехватим это здесь
            return new VsResponse { Success = false, Error = $"Roslyn Error: {ex.Message}\n{ex.StackTrace}" };
        }
    }

    public async Task<VsResponse> FindDeclarationsAsync(object globalServiceProvider, string symbolName)
    {
        // Уходим на фоновый поток ThreadPool
        await Task.Yield();

        try
        {
            var sp = (IServiceProvider)globalServiceProvider;
            var componentModel = (IComponentModel)sp.GetService(typeof(SComponentModel));
            if (componentModel == null)
            {
                return new VsResponse { Success = false, Error = "ComponentModel service not found." };
            }

            var workspace = componentModel.GetService<VisualStudioWorkspace>();
            if (workspace == null)
            {
                return new VsResponse { Success = false, Error = "VisualStudioWorkspace not found." };
            }

            var solution = workspace.CurrentSolution;

            // Находим декларации символов по текстовому имени во всем решении
            var symbols = (await SymbolFinder.FindSourceDeclarationsWithPatternAsync(
                solution,
                symbolName,
                SymbolFilter.Type | SymbolFilter.Member)).ToList();

            if (symbols.Count == 0)
            {
                return new VsResponse { Success = false, Error = $"Symbol '{symbolName}' isn't found." };
            }

            // Собираем строки с информацией о декларациях
            var resultLines = symbols.Select(s =>
                $"{s.Name} | {s.Kind} | {s.Locations.FirstOrDefault()?.SourceTree?.FilePath}")
                .ToList();

            return new VsResponse { Payload = string.Join("\n", resultLines) };
        }
        catch (Exception ex)
        {
            return new VsResponse { Success = false, Error = $"Roslyn Error: {ex.Message}\n{ex.StackTrace}" };
        }
    }
}

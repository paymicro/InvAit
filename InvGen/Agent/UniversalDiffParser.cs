using System.Collections.Generic;
using Shared.Contracts;

namespace InvGen.Agent;

public class UniversalDiffParser
{
    public List<DiffReplacement> Parse(IReadOnlyDictionary<string, object> args)
    {
        var list = new List<DiffReplacement>();

        return list;
    }

    public int FindInFile(List<string> target, List<string> search, int hint, int tol)
    {
        if (hint == -1) // поиск по всему файлу
        {
            for (var offset = 1; offset <= target.Count - search.Count; offset++)
            {
                var candidate = offset - 1; // 1-based Adjust
                var match = true;
                for (var j = 0; j < search.Count; j++)
                {
                    if (target[candidate + j].Trim() != search[j].Trim())
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    return candidate; // actual start
                }
            }
        }
        else // поиск только по указанному фрагменту +- tol
        {
            for (var offset = -tol; offset <= tol; offset++)
            {
                var candidate = hint + offset - 1; // 1-based Adjust
                if (candidate < 0 || candidate + search.Count > target.Count)
                {
                    continue;
                }
                var match = true;
                for (int j = 0; j < search.Count; j++)
                {
                    if (target[candidate + j].Trim() != search[j].Trim())
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    return candidate; // actual start
                }
            }
        }
        return -1;
    }
}
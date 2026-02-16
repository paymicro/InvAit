using System.Collections.Generic;

namespace InvAit.Agent;

public class UniversalDiffParser
{
    public int FindInFile(List<string> target, List<string> search, int hint, int tol)
    {
        if (hint == -1) // поиск по всему файлу
        {
            for (var i = 0; i <= target.Count - search.Count; i++)
            {
                var match = true;
                for (var j = 0; j < search.Count; j++)
                {
                    if (target[i + j].Trim() != search[j].Trim())
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    return i; // actual start
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
            
            // Fallback to full search if hint failed
            return FindInFile(target, search, -1, tol);
        }
        return -1;
    }
}

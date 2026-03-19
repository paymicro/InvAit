using System;
using System.Collections.Generic;

namespace InvAit.Agent;

/// <summary>
/// Универсальный парсер для поиска вхождений строк в тексте файла.
/// Используется для применения diff-изменений.
/// </summary>
public class UniversalDiffParser
{
    /// <summary>
    /// Проверяет совпадение строк начиная с заданного индекса.
    /// </summary>
    /// <param name="startIdx">Начальный индекс в target</param>
    /// <param name="target">Список строк файла</param>
    /// <param name="search">Список строк для поиска</param>
    /// <returns>true если все строки совпадают</returns>
    private static bool IsMatch(int startIdx, List<string> target, List<string> search)
    {
        for (var j = 0; j < search.Count; j++)
        {
            // Используем OrdinalIgnoreCase, чтобы не спотыкаться на регистре
            // Trim() убирает пробелы и невидимые символы переноса
            if (!string.Equals(target[startIdx + j].Trim(), search[j].Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Ищет вхождения списка строк в целевом файле.
    /// </summary>
    /// <param name="target">Список строк файла для поиска</param>
    /// <param name="search">Список строк для поиска</param>
    /// <param name="hint">1-based подсказка позиции (или -1 для полного поиска)</param>
    /// <param name="tol">Допуск смещения от hint</param>
    /// <returns>0-based индекс первого вхождения или -1 если не найдено</returns>
    public int FindInFile(List<string> target, List<string> search, int hint, int tol)
    {
        // Защита от null
        if (target == null || search == null)
            return -1;

        // Защита от пустого search
        if (search.Count == 0)
            return -1;

        // Защита: search длиннее target
        if (search.Count > target.Count)
            return -1;

        // Защита от отрицательного tolerance
        if (tol < 0)
            tol = 0;

        // 1. Поиск по хинту (1-based)
        if (hint != -1)
        {
            // Оптимизация: для однострочного поиска проверяем точный hint первым
            if (search.Count == 1)
            {
                var exactCandidate = hint - 1;
                if (exactCandidate >= 0 && exactCandidate < target.Count)
                {
                    if (IsMatch(exactCandidate, target, search))
                        return exactCandidate;
                }
            }

            for (var offset = -tol; offset <= tol; offset++)
            {
                var candidate = (hint - 1) + offset; // Приводим 1-based hint к 0-based индексу
                if (candidate >= 0 && candidate <= target.Count - search.Count)
                {
                    if (IsMatch(candidate, target, search))
                    {
                        return candidate; // Возвращаем 0-based индекс
                    }
                }
            }
            // Если в радиусе tol не нашли, идем в полный поиск
            return FindInFile(target, search, -1, tol);
        }

        // 2. Полный поиск по файлу
        for (var i = 0; i <= target.Count - search.Count; i++)
        {
            if (IsMatch(i, target, search))
            {
                return i;
            }
        }

        return -1;
    }
}

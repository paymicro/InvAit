using System.Runtime.CompilerServices;

namespace UIBlazor.Tests;

/// <summary>
/// Assembly-wide test defaults. Runs before any test is executed (ModuleInitializer is called
/// when the test module is loaded), so tests never depend on the order they run in.
/// </summary>
internal static class TestSetup
{
    /// <summary>
    /// Headless-мок по умолчанию имитирует медленный тулз (10 s) — это удобно для ручной
    /// проверки UI, но неприемлемо для тестов: задержка попадает внутрь измеряемого интервала
    /// исполнения (start → result). Гасим её один раз для всего набора.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        HeadlessMocker.DebugToolDelayMs = 0;
    }
}

using System.Runtime.CompilerServices;
using MasterDocumentation.Utilities;

// Все тесты работают с одним и тем же каталогом данных приложения (AppPaths.Data),
// поэтому классы тестов не должны выполняться параллельно.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MasterDocumentation.Tests;

internal static class TestStorage
{
    /// <summary>
    /// Хранилище приложения по умолчанию — папка самого приложения; для тестов это папка сборки,
    /// которую они очищают между запусками. Поэтому тесты работают с отдельным каталогом.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize() => AppPaths.UseDataLocation(Path.Combine(AppContext.BaseDirectory, "TestData"));
}

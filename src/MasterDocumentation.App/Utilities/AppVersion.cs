using System.Reflection;

namespace MasterDocumentation.Utilities;

/// <summary>
/// Версия приложения для показа пользователю. Берётся информационная версия, а не версия сборки:
/// только в ней остаётся суффикс предрелиза («1.2.0-beta»), по которому видно, что копия бета.
/// </summary>
public static class AppVersion
{
    public static string Display { get; } = Read();

    /// <summary>Предрелиз — любая версия с суффиксом: бета, rc и подобные.</summary>
    public static bool IsPreRelease { get; } = Display.Contains('-');

    private static string Read()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Сборка может дописать к версии метаданные вида «+abc1234» — пользователю они не нужны.
            var metadata = informational.IndexOf('+');
            return (metadata >= 0 ? informational[..metadata] : informational).Trim();
        }
        var version = assembly.GetName().Version;
        return version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}

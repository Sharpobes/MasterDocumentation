using System.IO;
namespace MasterDocumentation.Utilities;

public static class AppPaths
{
    public static string Root => AppContext.BaseDirectory;
    private static string _data=LoadConfiguredDataPath();
    public static string Data => _data;
    /// <summary>
    /// Служебные файлы среды выполнения: локальный редактор и фиксированный WebView2 Runtime.
    /// Они собраны в одну папку, чтобы рядом с MasterDocumentation.exe оставались только
    /// понятные пользователю папки хранилища.
    /// </summary>
    public static string Runtime => Path.Combine(Root, "Runtime");
    public static string Database => Path.Combine(Data, "master-documentation.db");
    public static string Assets => Path.Combine(Data, "Assets");
    /// <summary>Зеркало страниц документации в формате Markdown (.md).</summary>
    public static string Documents => Path.Combine(Data, "Documents");
    public static string Logs => Path.Combine(Data, "Logs");
    public static string Backups => Path.Combine(Data, "Backups");
    public static string Exports => Path.Combine(Data, "Exports");
    public static string Temp => Path.Combine(Data, "Temp");
    public static string Settings => Path.Combine(Data, "settings.json");
    public static string LocationConfig => Path.Combine(Root,"data-location.txt");

    /// <summary>Папки хранилища. Порядок задаёт и создание, и перенос данных.</summary>
    public static IReadOnlyList<string> DataDirectories => [Assets, Documents, Logs, Backups, Exports, Temp];

    /// <summary>
    /// Всё содержимое хранилища явным списком. Хранилище по умолчанию лежит в папке приложения,
    /// поэтому копировать, измерять и удалять его каталогом целиком нельзя — рядом лежат сам
    /// EXE и папка Runtime.
    /// </summary>
    public static IEnumerable<string> DataEntries
    {
        get
        {
            foreach (var directory in DataDirectories) yield return directory;
            yield return Database;
            yield return Settings;
            yield return Database + "-wal";
            yield return Database + "-shm";
        }
    }

    public static void Ensure()
    {
        Directory.CreateDirectory(Data);
        foreach (var path in DataDirectories) Directory.CreateDirectory(path);
    }

    /// <summary>Меняет хранилище только для текущего процесса, ничего не записывая на диск.</summary>
    public static void UseDataLocation(string path)=>_data=Path.GetFullPath(path);

    public static void SaveDataLocation(string path,bool activateForCurrentProcess=false)
    {
        var full=Path.GetFullPath(path);Directory.CreateDirectory(full);var temporary=LocationConfig+".tmp";File.WriteAllText(temporary,full);File.Move(temporary,LocationConfig,true);if(activateForCurrentProcess)_data=full;
    }

    /// <summary>
    /// Переносит хранилище прежних версий из подпапки «Data» в папку приложения: документы,
    /// вложения, журналы и резервные копии теперь лежат рядом с EXE, а не за лишним уровнем.
    /// Выполняется только для портативной копии — у установленной хранилище задано
    /// файлом data-location.txt и не трогается.
    /// </summary>
    public static void MigrateLegacyDataFolder()
    {
        try
        {
            if (File.Exists(LocationConfig)) return;
            var legacy = Path.Combine(Root, "Data");
            if (!Directory.Exists(legacy)) return;
            if (!Path.GetFullPath(Data).TrimEnd(Path.DirectorySeparatorChar)
                    .Equals(Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return;
            // Перенос делается один раз: если база уже в корне, старая папка не трогается.
            if (File.Exists(Database)) return;
            foreach (var entry in Directory.EnumerateFileSystemEntries(legacy))
            {
                var target = Path.Combine(Root, Path.GetFileName(entry));
                if (Directory.Exists(entry))
                {
                    if (Directory.Exists(target)) continue;
                    Directory.Move(entry, target);
                }
                else if (!File.Exists(target)) File.Move(entry, target);
            }
            if (!Directory.EnumerateFileSystemEntries(legacy).Any()) Directory.Delete(legacy);
        }
        catch
        {
            // Не удалось перенести — приложение продолжит работу с пустым хранилищем в корне,
            // а прежние файлы останутся в папке Data и не будут потеряны.
        }
    }

    private static string LoadConfiguredDataPath()
    {
        // Хранилище портативной копии лежит прямо в папке приложения: вложения, документы,
        // журналы, резервные копии и настройки видно сразу, без захода в подпапку.
        var fallback=Root.TrimEnd(Path.DirectorySeparatorChar);try{if(!File.Exists(Path.Combine(Root,"data-location.txt")))return fallback;var value=File.ReadAllText(Path.Combine(Root,"data-location.txt")).Trim();return string.IsNullOrWhiteSpace(value)?fallback:Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));}catch{return fallback;}
    }
}

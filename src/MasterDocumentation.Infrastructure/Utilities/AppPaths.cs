using System.IO;
namespace MasterDocumentation.Utilities;

public static class AppPaths
{
    public static string Root => AppContext.BaseDirectory;
    private static string _data=LoadConfiguredDataPath();
    public static string Data => _data;
    public static string Database => Path.Combine(Data, "master-documentation.db");
    public static string Assets => Path.Combine(Data, "Assets");
    /// <summary>Зеркало страниц документации в формате Markdown (.md).</summary>
    public static string Documents => Path.Combine(Data, "Documents");
    public static string Logs => Path.Combine(Data, "Logs");
    public static string Backups => Path.Combine(Data, "Backups");
    public static string Exports => Path.Combine(Data, "Exports");
    public static string Temp => Path.Combine(Data, "Temp");
    public static string Editor => Path.Combine(Data, "Editor");
    public static string Settings => Path.Combine(Data, "settings.json");
    public static string LocationConfig => Path.Combine(Root,"data-location.txt");

    public static void Ensure()
    {
        foreach (var path in new[] { Data, Assets, Documents, Logs, Backups, Exports, Temp, Editor }) Directory.CreateDirectory(path);
    }

    public static void SaveDataLocation(string path,bool activateForCurrentProcess=false)
    {
        var full=Path.GetFullPath(path);Directory.CreateDirectory(full);var temporary=LocationConfig+".tmp";File.WriteAllText(temporary,full);File.Move(temporary,LocationConfig,true);if(activateForCurrentProcess)_data=full;
    }

    private static string LoadConfiguredDataPath()
    {
        var fallback=Path.Combine(Root,"Data");try{if(!File.Exists(Path.Combine(Root,"data-location.txt")))return fallback;var value=File.ReadAllText(Path.Combine(Root,"data-location.txt")).Trim();return string.IsNullOrWhiteSpace(value)?fallback:Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));}catch{return fallback;}
    }
}

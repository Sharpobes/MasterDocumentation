using System.IO;
namespace MasterDocumentation.Utilities;

public static class AppPaths
{
    public static string Root => AppContext.BaseDirectory;
    public static string Data => Path.Combine(Root, "Data");
    public static string Database => Path.Combine(Data, "master-documentation.db");
    public static string Assets => Path.Combine(Data, "Assets");
    public static string Logs => Path.Combine(Data, "Logs");
    public static string Backups => Path.Combine(Data, "Backups");
    public static string Exports => Path.Combine(Data, "Exports");
    public static string Temp => Path.Combine(Data, "Temp");
    public static string Editor => Path.Combine(Data, "Editor");
    public static string Settings => Path.Combine(Data, "settings.json");

    public static void Ensure()
    {
        foreach (var path in new[] { Data, Assets, Logs, Backups, Exports, Temp, Editor }) Directory.CreateDirectory(path);
    }
}

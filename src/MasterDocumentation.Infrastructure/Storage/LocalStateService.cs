using System.IO;
using System.Text.Json;
using MasterDocumentation.Services;
using MasterDocumentation.Utilities;

namespace MasterDocumentation.Storage;

/// <summary>
/// Локальное состояние интерфейса: размеры и положение окна, ширина панелей, раскрытые узлы
/// дерева, дата последней автоматической резервной копии и т.п.
///
/// Это данные рабочего места, а не документации: в общую базу они не попадают ни при работе с
/// PostgreSQL, ни при переносе. В базе остаётся только то, что относится к самой документации
/// (документы, папки, изображения и вложения, теги, статусы, свойства, ссылки, версии).
/// Хранится в <c>ui-state.json</c> рядом с остальными данными приложения.
/// </summary>
public static class LocalStateService
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static Dictionary<string, string>? _values;

    public static string FilePath => Path.Combine(AppPaths.Data, "ui-state.json");

    public static string? Get(string key)
    {
        lock (Sync)
        {
            return Load().TryGetValue(key, out var value) ? value : null;
        }
    }

    public static void Set(string key, string value)
    {
        lock (Sync)
        {
            var values = Load();
            if (values.TryGetValue(key, out var current) && current == value) return;
            values[key] = value;
            Save(values);
        }
    }

    /// <summary>
    /// Разовый перенос ранее сохранённого состояния интерфейса из таблицы Settings локальной
    /// базы SQLite (там оно лежало до появления этого файла), чтобы пользователь не потерял
    /// размеры окна и раскрытые папки после обновления.
    /// </summary>
    public static void ImportLegacy(Func<string, string?> read)
    {
        lock (Sync)
        {
            if (File.Exists(FilePath)) return;
            var values = Load();
            foreach (var key in LegacyKeys)
            {
                var value = read(key);
                if (!string.IsNullOrEmpty(value)) values[key] = value;
            }
            if (values.Count > 0) Save(values);
        }
    }

    private static readonly string[] LegacyKeys =
    [
        "ExpandedNodeIds", "LastAutoBackup", "Width", "Height", "Left", "Top",
        "LibraryWidth", "StructureWidth", "PropertiesWidth",
        "LibraryPanelCollapsed", "StructurePanelCollapsed", "PropertiesPanelCollapsed",
    ];

    private static Dictionary<string, string> Load()
    {
        if (_values is not null) return _values;
        try
        {
            _values = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception ex)
        {
            LogService.Error("Не удалось прочитать локальное состояние интерфейса", ex);
            _values = [];
        }
        return _values;
    }

    private static void Save(Dictionary<string, string> values)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(values, JsonOptions));
            File.Move(temporary, FilePath, true);
        }
        catch (Exception ex) { LogService.Error("Не удалось сохранить локальное состояние интерфейса", ex); }
    }
}

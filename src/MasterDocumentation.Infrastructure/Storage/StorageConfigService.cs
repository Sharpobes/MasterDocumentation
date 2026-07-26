using System.IO;
using System.Text.Json;
using MasterDocumentation.Utilities;

namespace MasterDocumentation.Storage;

/// <summary>
/// Загрузка/сохранение выбора провайдера хранения (SQLite/Postgres/...).
/// Файл конфигурации лежит рядом с data-location.txt в корне приложения, чтобы быть
/// читаемым до создания DI-контейнера и DatabaseService.
/// </summary>
public static class StorageConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string ConfigPath => Path.Combine(AppPaths.Root, "storage-provider.json");

    public static StorageProviderConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new StorageProviderConfig();
            var config = JsonSerializer.Deserialize<StorageProviderConfig>(File.ReadAllText(ConfigPath), JsonOptions);
            return config ?? new StorageProviderConfig();
        }
        catch { return new StorageProviderConfig(); }
    }

    public static void Save(StorageProviderConfig config)
    {
        var temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temp, ConfigPath, true);
    }

    /// <summary>Проверка соединения без изменения текущей активной конфигурации.</summary>
    public static bool TestConnection(StorageProviderConfig config, out string error)
    {
        error = "";
        try
        {
            switch (config.Provider)
            {
                case StorageProviderKind.Postgres:
                    using (var connection = new Npgsql.NpgsqlConnection(config.PostgresConnectionString))
                    {
                        connection.Open();
                    }
                    return true;
                default:
                    return true;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

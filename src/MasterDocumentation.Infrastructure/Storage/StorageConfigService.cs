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
    public static bool TestConnection(StorageProviderConfig config, out string error) => TestConnection(config, out error, out _);

    /// <summary>
    /// Проверка соединения. Отсутствующая база не считается ошибкой: приложение создаёт её
    /// вместе со всеми таблицами самостоятельно (<see cref="PostgresProvisioning"/>), поэтому
    /// такой случай возвращается отдельным флагом <paramref name="databaseMissing"/>.
    /// </summary>
    public static bool TestConnection(StorageProviderConfig config, out string error, out bool databaseMissing)
        => TestConnection(config, out error, out databaseMissing, out _);

    /// <summary>
    /// То же самое, но с исходным исключением: интерфейс показывает по нему развёрнутое
    /// описание (<see cref="PostgresErrorInfo.Detailed"/>) с кодом ошибки сервера и подсказкой.
    /// </summary>
    public static bool TestConnection(StorageProviderConfig config, out string error, out bool databaseMissing, out Exception? exception)
    {
        error = "";
        databaseMissing = false;
        exception = null;
        try
        {
            switch (config.Provider)
            {
                case StorageProviderKind.Postgres:
                    if (!PostgresConnectionString.TryValidate(config.PostgresConnectionString, out error)) return false;
                    databaseMissing = !PostgresProvisioning.DatabaseExists(config.PostgresConnectionString);
                    return true;
                default:
                    return true;
            }
        }
        catch (Exception ex)
        {
            exception = ex;
            error = PostgresErrorInfo.Short(ex);
            return false;
        }
    }
}

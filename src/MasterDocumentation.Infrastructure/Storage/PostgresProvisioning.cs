using Npgsql;
using MasterDocumentation.Services;

namespace MasterDocumentation.Storage;

/// <summary>
/// Автоматическая подготовка внешней базы PostgreSQL: приложению достаточно строки подключения,
/// всё остальное (сама база, расширения, таблицы и индексы) создаётся кодом.
///
///  - <see cref="EnsureDatabase"/> создаёт базу из строки подключения, если её ещё нет:
///    подключается к служебной базе (postgres/template1) под теми же учётными данными и
///    выполняет CREATE DATABASE. Требуется только право CREATEDB у пользователя;
///  - <see cref="EnsureExtension"/> ставит расширение, но не считает отказ ошибкой: если прав нет,
///    схема создаётся в «переносимом» виде (см. <see cref="PostgresDocumentStore"/>);
///  - схему таблиц создаёт сам <see cref="PostgresDocumentStore.Initialize"/> — отдельного шага
///    «создать модели вручную» у пользователя нет.
/// </summary>
public static class PostgresProvisioning
{
    private const string InvalidCatalogName = "3D000";
    private const string DuplicateDatabase = "42P04";

    /// <summary>
    /// Проверяет доступность базы из строки подключения и создаёт её при отсутствии.
    /// Возвращает true, если база была создана этим вызовом.
    /// </summary>
    public static bool EnsureDatabase(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var database = builder.Database;
        if (string.IsNullOrWhiteSpace(database)) throw new InvalidOperationException("В строке подключения не указана база данных (Database=...).");

        try
        {
            using var probe = new NpgsqlConnection(connectionString);
            probe.Open();
            return false;
        }
        catch (PostgresException ex) when (ex.SqlState == InvalidCatalogName)
        {
            // База ещё не создана — создаём её сами, чтобы пользователю не приходилось идти в psql/pgAdmin.
        }

        foreach (var maintenance in MaintenanceDatabases(builder))
        {
            try
            {
                using var connection = new NpgsqlConnection(MaintenanceConnectionString(builder, maintenance));
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"CREATE DATABASE {Quote(database!)}";
                command.ExecuteNonQuery();
                LogService.Info($"Создана база данных PostgreSQL «{database}»");
                return true;
            }
            catch (PostgresException ex) when (ex.SqlState == DuplicateDatabase)
            {
                return false;
            }
            catch (Exception ex)
            {
                LogService.Error($"Не удалось создать базу «{database}» через служебную базу «{maintenance}»", ex);
            }
        }
        throw new InvalidOperationException($"База «{database}» не существует, и создать её не удалось. Проверьте, что у пользователя есть право CREATEDB, либо создайте базу вручную.");
    }

    /// <summary>Ставит расширение, если это разрешено правами пользователя. Возвращает доступность расширения.</summary>
    public static bool EnsureExtension(NpgsqlConnection connection, string name)
    {
        try
        {
            using var create = connection.CreateCommand();
            create.CommandText = $"CREATE EXTENSION IF NOT EXISTS {name}";
            create.ExecuteNonQuery();
            return true;
        }
        catch (PostgresException ex)
        {
            LogService.Error($"Расширение PostgreSQL «{name}» недоступно ({ex.SqlState}), используется переносимый вариант схемы", ex);
            return HasExtension(connection, name);
        }
    }

    /// <summary>
    /// Перечитывает каталог типов сервера. Npgsql читает список типов один раз — при первом
    /// подключении к строке подключения — и кеширует его глобально. Если расширение (citext) или
    /// таблицы создаются уже после этого, тип citext остаётся клиенту неизвестным, и любое чтение
    /// такой колонки падает с InvalidCastException «…not supported for fields having DataTypeName
    /// '.&lt;unknown&gt;'». Поэтому после создания схемы кеш типов сбрасывается принудительно.
    /// </summary>
    public static void RefreshTypeCache(NpgsqlConnection connection)
    {
        try
        {
            connection.ReloadTypes();
            NpgsqlConnection.ClearAllPools();
        }
        catch (Exception ex)
        {
            LogService.Error("Не удалось перечитать каталог типов PostgreSQL", ex);
        }
    }

    public static bool HasExtension(NpgsqlConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_extension WHERE extname=@n)";
        command.Parameters.AddWithValue("n", name);
        return command.ExecuteScalar() is true;
    }

    /// <summary>Существует ли база из строки подключения (сервер при этом должен быть доступен).</summary>
    public static bool DatabaseExists(string connectionString)
    {
        try
        {
            using var probe = new NpgsqlConnection(connectionString);
            probe.Open();
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == InvalidCatalogName)
        {
            return false;
        }
    }

    private static IEnumerable<string> MaintenanceDatabases(NpgsqlConnectionStringBuilder builder)
    {
        yield return "postgres";
        yield return "template1";
        if (!string.IsNullOrWhiteSpace(builder.Username) && builder.Username != "postgres") yield return builder.Username!;
    }

    private static string MaintenanceConnectionString(NpgsqlConnectionStringBuilder source, string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(source.ConnectionString) { Database = database, Pooling = false };
        return builder.ConnectionString;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}

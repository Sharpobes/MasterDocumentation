namespace MasterDocumentation.Storage;

/// <summary>
/// Поддерживаемые провайдеры хранения документации. Новый провайдер добавляется путём
/// реализации <see cref="IDocumentStore"/> и добавления соответствующего значения сюда —
/// без изменений в UI и вызывающем коде, кроме одной ветки выбора в DatabaseService.
/// </summary>
public enum StorageProviderKind
{
    Sqlite = 0,
    Postgres = 1,
}

/// <summary>
/// Персистентная конфигурация выбора провайдера хранения. Хранится вне settings.json,
/// т.к. должна быть читаема максимально рано (до инициализации DI/DatabaseService),
/// аналогично AppPaths.LocationConfig.
/// </summary>
public sealed class StorageProviderConfig
{
    public StorageProviderKind Provider { get; set; } = StorageProviderKind.Sqlite;
    public string PostgresConnectionString { get; set; } = "";
}

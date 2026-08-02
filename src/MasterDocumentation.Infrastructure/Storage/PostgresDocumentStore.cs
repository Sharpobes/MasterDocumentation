using Npgsql;
using System.IO;
using MasterDocumentation.Models;
using MasterDocumentation.Services;
using MasterDocumentation.Utilities;
using System.Windows;
using System.Windows.Documents;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MasterDocumentation.Storage;

/// <summary>
/// Провайдер хранения документации на PostgreSQL. Реализует тот же контракт
/// <see cref="IDocumentStore"/>, что и <see cref="SqliteDocumentStore"/>, с сохранением
/// текстового ISO-8601 формата дат (чтобы не менять вызывающий код) и построчным переносом
/// логики SQLite-версии на диалект Postgres. Ключевые отличия перевода зафиксированы ниже:
///
///  - AUTOINCREMENT/last_insert_rowid() → GENERATED ALWAYS AS IDENTITY + RETURNING Id;
///  - GUID генерируются через gen_random_uuid()::text (в Create — на стороне C#, как и раньше);
///  - "IS $param" (null-safe сравнение SQLite) → "IS NOT DISTINCT FROM @param";
///  - COLLATE NOCASE (Title/Statuses.Name/Tags.Name) → тип CITEXT для этих колонок. Это чуть
///    шире, чем в SQLite (там NOCASE применялся только к конкретным запросам), но семантически
///    ближе всего и не требует дублировать ILIKE/lower() по всем местам использования;
///  - FTS5 (MATCH/bm25/snippet) → tsvector/GIN-индекс + ts_rank_cd/ts_headline с теми же
///    маркерами подсветки '⟦'/'⟧'. Точное ранжирование будет отличаться от bm25 — это
///    приближённый, но рабочий перевод, который стоит проверить на реальном Postgres;
///  - group_concat → string_agg; char(10) → chr(10); X'' → ''::bytea;
///  - PRAGMA integrity_check/wal_checkpoint не имеют прямых аналогов для обычного
///    пользователя — CheckIntegrity делает лёгкую проверку соединения, Checkpoint — best-effort
///    CHECKPOINT, который тихо игнорируется при нехватке прав.
/// </summary>
public sealed class PostgresDocumentStore : IDocumentStore
{
    private readonly string _connectionString;

    public PostgresDocumentStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("Не указана строка подключения к PostgreSQL.");
        _connectionString = connectionString;
    }

    private NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        return connection;
    }

    // Приватные страницы видит только их владелец: условие подставляется во все выборки,
    // возвращающие список или содержимое документов. Все остальные страницы доступны каждому,
    // кто подключился к этой базе через приложение.
    private const string AccessFilter = "(IsPrivate=FALSE OR Owner='' OR lower(Owner)=lower(@user))";
    private const string AccessFilterAliased = "(n.IsPrivate=FALSE OR n.Owner='' OR lower(n.Owner)=lower(@user))";
    private static void AddUser(NpgsqlCommand command) => command.Parameters.AddWithValue("user", UserIdentity.Current);

    private static void AddNullableLong(NpgsqlCommand command, string name, long? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = System.Data.DbType.Int64;
        parameter.Value = (object?)value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    public void Initialize()
    {
        using var connection = Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE EXTENSION IF NOT EXISTS citext;
                CREATE TABLE IF NOT EXISTS Nodes(
                  Id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                  ParentId BIGINT NULL REFERENCES Nodes(Id) ON DELETE CASCADE,
                  IsFolder BOOLEAN NOT NULL,
                  Title CITEXT NOT NULL,
                  SortOrder INTEGER NOT NULL DEFAULT 0,
                  CreatedAt TEXT NOT NULL,
                  ModifiedAt TEXT NOT NULL,
                  Content BYTEA NULL,
                  PlainText TEXT NOT NULL DEFAULT ''
                );
                CREATE INDEX IF NOT EXISTS IX_Nodes_Parent ON Nodes(ParentId, SortOrder);
                CREATE TABLE IF NOT EXISTS Settings(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS SchemaMigrations(Version INTEGER PRIMARY KEY, AppliedAt TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS DocumentVersions(
                  Id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                  DocumentId BIGINT NOT NULL REFERENCES Nodes(Id) ON DELETE CASCADE,
                  CreatedAt TEXT NOT NULL, Reason TEXT NOT NULL, Content BYTEA NOT NULL, PlainText TEXT NOT NULL, Size BIGINT NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_DocumentVersions_Document ON DocumentVersions(DocumentId, CreatedAt DESC);
                CREATE TABLE IF NOT EXISTS Attachments(
                  Id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                  DocumentId BIGINT NOT NULL REFERENCES Nodes(Id) ON DELETE CASCADE,
                  FileName TEXT NOT NULL, StoredName TEXT NOT NULL, MimeType TEXT NOT NULL, Size BIGINT NOT NULL, Sha256 TEXT NOT NULL, CreatedAt TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_Attachments_Document ON Attachments(DocumentId, CreatedAt DESC);
                CREATE INDEX IF NOT EXISTS IX_Attachments_Hash ON Attachments(Sha256);
                CREATE TABLE IF NOT EXISTS Tags(Id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY, Name CITEXT NOT NULL UNIQUE);
                CREATE TABLE IF NOT EXISTS DocumentTags(DocumentId BIGINT NOT NULL REFERENCES Nodes(Id) ON DELETE CASCADE, TagId BIGINT NOT NULL REFERENCES Tags(Id) ON DELETE CASCADE, PRIMARY KEY(DocumentId,TagId));
                CREATE TABLE IF NOT EXISTS CustomProperties(Id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY, DocumentId BIGINT NOT NULL REFERENCES Nodes(Id) ON DELETE CASCADE, Name TEXT NOT NULL, Value TEXT NOT NULL, UNIQUE(DocumentId,Name));
                CREATE TABLE IF NOT EXISTS Statuses(Name CITEXT PRIMARY KEY, SortOrder INTEGER NOT NULL DEFAULT 0, IsBuiltIn BOOLEAN NOT NULL DEFAULT FALSE);
                CREATE TABLE IF NOT EXISTS Assets(StoredName TEXT PRIMARY KEY, Sha256 TEXT NOT NULL, MimeType TEXT NOT NULL DEFAULT '', Size BIGINT NOT NULL, Data BYTEA NOT NULL, CreatedAt TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_Assets_Hash ON Assets(Sha256);
                INSERT INTO Statuses(Name,SortOrder,IsBuiltIn) VALUES('Черновик',0,TRUE),('В работе',1,TRUE),('На проверке',2,TRUE),('Завершён',3,TRUE),('Архив',4,TRUE) ON CONFLICT (Name) DO NOTHING;
                INSERT INTO SchemaMigrations(Version,AppliedAt) VALUES(1,@now) ON CONFLICT (Version) DO NOTHING;
                """;
            command.Parameters.AddWithValue("now", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        EnsureColumn(connection, "Nodes", "IsFavorite", "BOOLEAN NOT NULL DEFAULT FALSE");
        EnsureColumn(connection, "Nodes", "DeletedAt", "TEXT NULL");
        EnsureColumn(connection, "Nodes", "LastOpenedAt", "TEXT NULL");
        EnsureColumn(connection, "Nodes", "Status", "TEXT NOT NULL DEFAULT 'Черновик'");
        EnsureColumn(connection, "Nodes", "EditorJson", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Nodes", "Html", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Nodes", "IsTemplate", "BOOLEAN NOT NULL DEFAULT FALSE");
        EnsureColumn(connection, "Nodes", "Zoom", "DOUBLE PRECISION NOT NULL DEFAULT 1.0");
        EnsureColumn(connection, "Nodes", "Guid", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Nodes", "Author", "TEXT NOT NULL DEFAULT 'Локальный пользователь'");
        EnsureColumn(connection, "Nodes", "TemplateSourceId", "BIGINT NULL");
        EnsureColumn(connection, "Nodes", "Markdown", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Nodes", "IsPrivate", "BOOLEAN NOT NULL DEFAULT FALSE");
        EnsureColumn(connection, "Nodes", "Owner", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "DocumentVersions", "EditorJson", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "DocumentVersions", "Html", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "DocumentVersions", "IsPinned", "BOOLEAN NOT NULL DEFAULT FALSE");
        using (var statuses = connection.CreateCommand())
        {
            statuses.CommandText = "INSERT INTO Statuses(Name,SortOrder,IsBuiltIn) SELECT DISTINCT Status,100,FALSE FROM Nodes WHERE trim(Status)<>'' ON CONFLICT (Name) DO NOTHING";
            statuses.ExecuteNonQuery();
        }
        using (var identities = connection.CreateCommand())
        {
            identities.CommandText = "UPDATE Nodes SET Guid=gen_random_uuid()::text WHERE Guid=''; CREATE UNIQUE INDEX IF NOT EXISTS IX_Nodes_Guid ON Nodes(Guid);";
            identities.ExecuteNonQuery();
        }
        using (var search = connection.CreateCommand())
        {
            search.CommandText = """
                CREATE TABLE IF NOT EXISTS SearchIndex(
                  NodeId BIGINT PRIMARY KEY REFERENCES Nodes(Id) ON DELETE CASCADE,
                  Title TEXT NOT NULL,
                  PlainText TEXT NOT NULL,
                  SearchVector TSVECTOR GENERATED ALWAYS AS (to_tsvector('russian', coalesce(Title,'') || ' ' || coalesce(PlainText,''))) STORED
                );
                CREATE INDEX IF NOT EXISTS IX_SearchIndex_Vector ON SearchIndex USING GIN(SearchVector);
                DELETE FROM SearchIndex;
                INSERT INTO SearchIndex(NodeId,Title,PlainText)
                  SELECT n.Id, n.Title,
                    n.PlainText || ' ' || n.Status
                    || ' ' || COALESCE((SELECT Title FROM Nodes p WHERE p.Id=n.ParentId),'')
                    || ' ' || COALESCE((SELECT string_agg(t.Name,' ') FROM DocumentTags dt JOIN Tags t ON t.Id=dt.TagId WHERE dt.DocumentId=n.Id),'')
                    || ' ' || COALESCE((SELECT string_agg(a.FileName,' ') FROM Attachments a WHERE a.DocumentId=n.Id),'')
                  FROM Nodes n WHERE n.IsFolder=FALSE;
                """;
            search.ExecuteNonQuery();
        }
        LogService.Info("Хранилище инициализировано (PostgreSQL)");
    }

    public List<NodeItem> LoadTree(string? query = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        AddUser(command);
        if (string.IsNullOrWhiteSpace(query))
        {
            command.CommandText = $"SELECT Id,ParentId,IsFolder,Title,SortOrder,CreatedAt,ModifiedAt FROM Nodes WHERE DeletedAt IS NULL AND {AccessFilter} ORDER BY SortOrder,Title";
        }
        else
        {
            var term = query.Trim();
            command.CommandText = $"""
                WITH RECURSIVE matched(Id) AS (
                  SELECT n.Id FROM Nodes n
                  WHERE n.DeletedAt IS NULL AND {AccessFilterAliased} AND (n.Title ILIKE @q OR (n.IsFolder=FALSE AND n.Id IN (SELECT NodeId FROM SearchIndex WHERE SearchVector @@ to_tsquery('russian', @fts))))
                ), visible(Id) AS (
                  SELECT Id FROM matched
                  UNION
                  SELECT n.ParentId FROM Nodes n JOIN visible v ON n.Id=v.Id WHERE n.ParentId IS NOT NULL
                )
                SELECT DISTINCT n.Id,n.ParentId,n.IsFolder,n.Title,n.SortOrder,n.CreatedAt,n.ModifiedAt
                FROM Nodes n JOIN visible v ON v.Id=n.Id WHERE n.DeletedAt IS NULL AND {AccessFilterAliased} ORDER BY n.SortOrder,n.Title
                """;
            command.Parameters.AddWithValue("q", $"%{term}%");
            var fts = BuildTsQuery(term);
            command.Parameters.AddWithValue("fts", fts.Length > 0 ? fts : "' '");
        }
        var all = new List<NodeItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) all.Add(new NodeItem { Id = reader.GetInt64(0), ParentId = reader.IsDBNull(1) ? null : reader.GetInt64(1), IsFolder = reader.GetBoolean(2), Title = reader.GetString(3), SortOrder = reader.GetInt32(4), CreatedAt = DateTime.Parse(reader.GetString(5)), ModifiedAt = DateTime.Parse(reader.GetString(6)) });
        var byId = all.ToDictionary(x => x.Id);
        foreach (var node in all.Where(x => x.ParentId.HasValue && byId.ContainsKey(x.ParentId.Value))) byId[node.ParentId!.Value].Children.Add(node);
        return all.Where(x => !x.ParentId.HasValue || !byId.ContainsKey(x.ParentId.Value)).ToList();
    }

    public List<NodeItem> LoadFavorites() => LoadFlat("IsFolder=FALSE AND IsFavorite=TRUE AND DeletedAt IS NULL", "ModifiedAt DESC");
    public List<NodeItem> LoadRecent(int limit = 25) => LoadFlat("IsFolder=FALSE AND LastOpenedAt IS NOT NULL AND DeletedAt IS NULL", $"LastOpenedAt DESC LIMIT {Math.Clamp(limit, 1, 100)}");
    public List<NodeItem> LoadTrash() => LoadFlat("DeletedAt IS NOT NULL", "DeletedAt DESC");
    public List<NodeItem> LoadTemplates() => LoadFlat("IsFolder=FALSE AND IsTemplate=TRUE AND DeletedAt IS NULL", "Title");

    public NodeItem? FindNode(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Id,ParentId,IsFolder,Title,SortOrder,CreatedAt,ModifiedAt FROM Nodes WHERE Id=@id AND DeletedAt IS NULL AND {AccessFilter}";
        command.Parameters.AddWithValue("id", id);
        AddUser(command);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new NodeItem { Id = reader.GetInt64(0), ParentId = reader.IsDBNull(1) ? null : reader.GetInt64(1), IsFolder = reader.GetBoolean(2), Title = reader.GetString(3), SortOrder = reader.GetInt32(4), CreatedAt = DateTime.Parse(reader.GetString(5)), ModifiedAt = DateTime.Parse(reader.GetString(6)) } : null;
    }

    public long Create(long? parentId, bool folder, string title) => Create(parentId, folder, title, false);

    public long Create(long? parentId, bool folder, string title, bool isPrivate)
    {
        title = title.Trim();
        if (title.Length == 0) throw new ArgumentException("Название не может быть пустым.");
        if (TitleExists(parentId, title)) throw new InvalidOperationException($"В выбранной папке уже существует элемент «{title}».");
        var now = DateTime.UtcNow.ToString("O");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Nodes(ParentId,IsFolder,Title,SortOrder,CreatedAt,ModifiedAt,Guid,Author,IsPrivate,Owner) VALUES(@p,@f,@t,(SELECT COALESCE(MAX(SortOrder)+1,0) FROM Nodes WHERE ParentId IS NOT DISTINCT FROM @p),@c,@c,@g,@owner,@private,@owner) RETURNING Id";
        AddNullableLong(command, "p", parentId);
        command.Parameters.AddWithValue("f", folder);
        command.Parameters.AddWithValue("t", title);
        command.Parameters.AddWithValue("c", now);
        command.Parameters.AddWithValue("g", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("private", isPrivate);
        command.Parameters.AddWithValue("owner", UserIdentity.Current);
        return (long)command.ExecuteScalar()!;
    }

    public bool TitleExists(long? parentId, string title, long? exceptId = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM Nodes WHERE ParentId IS NOT DISTINCT FROM @p AND Title=@t AND DeletedAt IS NULL AND (@e::bigint IS NULL OR Id<>@e))";
        AddNullableLong(command, "p", parentId);
        command.Parameters.AddWithValue("t", title.Trim());
        AddNullableLong(command, "e", exceptId);
        return (bool)command.ExecuteScalar()!;
    }

    public void Rename(long id, string title)
    {
        var node = FindNode(id) ?? throw new InvalidOperationException("Элемент не найден.");
        if (TitleExists(node.ParentId, title, id)) throw new InvalidOperationException($"В выбранной папке уже существует элемент «{title}».");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Nodes SET Title=@v, ModifiedAt=@m WHERE Id=@id";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("v", title.Trim());
        command.Parameters.AddWithValue("m", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        if (node.IsFolder) RefreshBranchSearchIndex(id); else RefreshSearchIndex(id);
    }

    public void SetTemplate(long id, bool value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Nodes SET IsTemplate=@v, ModifiedAt=@d WHERE Id=@id AND IsFolder=FALSE";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("v", value);
        command.Parameters.AddWithValue("d", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public long CreateFromTemplate(long templateId, long? parentId, string title, IReadOnlyDictionary<string, string>? variables = null)
    {
        if (TitleExists(parentId, title)) throw new InvalidOperationException($"В выбранной папке уже существует документ «{title}».");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        long created;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Nodes(ParentId,IsFolder,Title,SortOrder,CreatedAt,ModifiedAt,Content,PlainText,Status,EditorJson,Html,Markdown,TemplateSourceId,Guid,Author,Owner)
                SELECT @p,FALSE,@t,(SELECT COALESCE(MAX(SortOrder)+1,0) FROM Nodes WHERE ParentId IS NOT DISTINCT FROM @p),@d,@d,Content,PlainText,'Черновик',EditorJson,Html,Markdown,Id,gen_random_uuid()::text,@owner,@owner
                FROM Nodes WHERE Id=@id AND IsTemplate=TRUE
                RETURNING Id
                """;
            AddNullableLong(command, "p", parentId);
            command.Parameters.AddWithValue("t", title.Trim());
            command.Parameters.AddWithValue("d", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("id", templateId);
            command.Parameters.AddWithValue("owner", UserIdentity.Current);
            var result = command.ExecuteScalar();
            if (result is null) throw new InvalidOperationException("Шаблон не найден.");
            created = (long)result;
        }
        using (var copy = connection.CreateCommand())
        {
            copy.Transaction = transaction;
            copy.CommandText = """
                INSERT INTO Attachments(DocumentId,FileName,StoredName,MimeType,Size,Sha256,CreatedAt)
                  SELECT @new,FileName,StoredName,MimeType,Size,Sha256,@d FROM Attachments WHERE DocumentId=@source;
                INSERT INTO DocumentTags(DocumentId,TagId)
                  SELECT @new,TagId FROM DocumentTags WHERE DocumentId=@source;
                """;
            copy.Parameters.AddWithValue("new", created);
            copy.Parameters.AddWithValue("source", templateId);
            copy.Parameters.AddWithValue("d", DateTime.UtcNow.ToString("O"));
            copy.ExecuteNonQuery();
        }
        ApplyTemplateVariables(connection, transaction, created, parentId, title, variables);
        transaction.Commit();
        RefreshSearchIndex(created);
        return created;
    }

    public IReadOnlyList<string> GetTemplateVariables(long templateId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EditorJson || chr(10) || Html || chr(10) || PlainText FROM Nodes WHERE Id=@id AND IsTemplate=TRUE";
        command.Parameters.AddWithValue("id", templateId);
        var source = command.ExecuteScalar() as string ?? throw new InvalidOperationException("Шаблон не найден.");
        return Regex.Matches(source, @"\{\{\s*([\p{L}\p{N}_ -]{1,80})\s*\}\}").Select(x => x.Groups[1].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void ApplyTemplateVariables(NpgsqlConnection connection, NpgsqlTransaction transaction, long documentId, long? parentId, string title, IReadOnlyDictionary<string, string>? custom)
    {
        var now = DateTime.Now;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "Title", title }, { "Date", now.ToString("d") }, { "Time", now.ToString("t") }, { "Author", "Локальный пользователь" }, { "Section", "" } };
        if (parentId is long parent)
        {
            using var parentCommand = connection.CreateCommand();
            parentCommand.Transaction = transaction;
            parentCommand.CommandText = "SELECT Title FROM Nodes WHERE Id=@id";
            parentCommand.Parameters.AddWithValue("id", parent);
            values["Section"] = parentCommand.ExecuteScalar() as string ?? "";
        }
        if (custom is not null) foreach (var item in custom) values[item.Key] = item.Value;
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT EditorJson,Html,PlainText FROM Nodes WHERE Id=@id";
        read.Parameters.AddWithValue("id", documentId);
        string json, html, plain;
        using (var reader = read.ExecuteReader())
        {
            if (!reader.Read()) return;
            json = reader.GetString(0); html = reader.GetString(1); plain = reader.GetString(2);
        }
        string Replace(string input, Func<string, string> encode) => Regex.Replace(input, @"\{\{\s*([\p{L}\p{N}_ -]{1,80})\s*\}\}", match => values.TryGetValue(match.Groups[1].Value.Trim(), out var value) ? encode(value) : match.Value);
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE Nodes SET EditorJson=@j, Html=@h, PlainText=@p WHERE Id=@id";
        update.Parameters.AddWithValue("id", documentId);
        update.Parameters.AddWithValue("j", Replace(json, value => JsonSerializer.Serialize(value)[1..^1]));
        update.Parameters.AddWithValue("h", Replace(html, System.Net.WebUtility.HtmlEncode));
        update.Parameters.AddWithValue("p", Replace(plain, value => value));
        update.ExecuteNonQuery();
    }

    public string GetDocumentGuid(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Guid FROM Nodes WHERE Id=@id AND IsFolder=FALSE";
        command.Parameters.AddWithValue("id", id);
        return command.ExecuteScalar() as string ?? throw new InvalidOperationException("Документ не найден.");
    }

    public NodeItem? FindDocumentByGuid(string guid)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Id,ParentId,IsFolder,Title,SortOrder,CreatedAt,ModifiedAt FROM Nodes WHERE Guid=@guid AND IsFolder=FALSE AND DeletedAt IS NULL AND {AccessFilter}";
        command.Parameters.AddWithValue("guid", guid);
        AddUser(command);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new NodeItem { Id = reader.GetInt64(0), ParentId = reader.IsDBNull(1) ? null : reader.GetInt64(1), IsFolder = false, Title = reader.GetString(3), SortOrder = reader.GetInt32(4), CreatedAt = DateTime.Parse(reader.GetString(5)), ModifiedAt = DateTime.Parse(reader.GetString(6)) } : null;
    }

    public void Move(long id, long? parentId)
    {
        var source = FindNode(id) ?? throw new InvalidOperationException("Перемещаемый элемент не найден.");
        if (parentId == id) throw new InvalidOperationException("Нельзя переместить элемент внутрь самого себя.");
        using var connection = Open();
        if (parentId is long parent)
        {
            using var target = connection.CreateCommand();
            target.CommandText = "SELECT IsFolder FROM Nodes WHERE Id=@id AND DeletedAt IS NULL";
            target.Parameters.AddWithValue("id", parent);
            var isFolder = target.ExecuteScalar();
            if (isFolder is not bool folderValue || !folderValue) throw new InvalidOperationException("Целевой элемент не является папкой.");
            using var cycle = connection.CreateCommand();
            cycle.CommandText = "WITH RECURSIVE branch(Id) AS (SELECT @id::bigint UNION ALL SELECT n.Id FROM Nodes n JOIN branch b ON n.ParentId=b.Id) SELECT EXISTS(SELECT 1 FROM branch WHERE Id=@parent)";
            cycle.Parameters.AddWithValue("id", id);
            cycle.Parameters.AddWithValue("parent", parent);
            if ((bool)cycle.ExecuteScalar()!) throw new InvalidOperationException("Нельзя переместить папку внутрь её дочернего элемента.");
        }
        if (TitleExists(parentId, source.Title, id)) throw new InvalidOperationException($"В целевой папке уже существует элемент «{source.Title}».");
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Nodes SET ParentId=@p, SortOrder=(SELECT COALESCE(MAX(SortOrder)+1,0) FROM Nodes WHERE ParentId IS NOT DISTINCT FROM @p AND Id<>@id), ModifiedAt=@m WHERE Id=@id";
        command.Parameters.AddWithValue("id", id);
        AddNullableLong(command, "p", parentId);
        command.Parameters.AddWithValue("m", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        if (source.IsFolder) RefreshBranchSearchIndex(id); else RefreshSearchIndex(id);
    }

    public void Delete(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "WITH RECURSIVE branch(Id) AS (SELECT @id::bigint UNION ALL SELECT n.Id FROM Nodes n JOIN branch b ON n.ParentId=b.Id) UPDATE Nodes SET DeletedAt=@d, ModifiedAt=@d WHERE Id IN (SELECT Id FROM branch)";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("d", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Restore(long id)
    {
        using var connection = Open();
        long? parent; string original;
        using (var find = connection.CreateCommand())
        {
            find.CommandText = "SELECT ParentId,Title FROM Nodes WHERE Id=@id AND DeletedAt IS NOT NULL";
            find.Parameters.AddWithValue("id", id);
            using var reader = find.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("Элемент не найден в корзине.");
            parent = reader.IsDBNull(0) ? null : reader.GetInt64(0);
            original = reader.GetString(1);
        }
        var title = original;
        for (var i = 2; ; i++)
        {
            using var conflict = connection.CreateCommand();
            conflict.CommandText = "SELECT EXISTS(SELECT 1 FROM Nodes WHERE ParentId IS NOT DISTINCT FROM @p AND Title=@t AND DeletedAt IS NULL AND Id<>@id)";
            AddNullableLong(conflict, "p", parent);
            conflict.Parameters.AddWithValue("t", title);
            conflict.Parameters.AddWithValue("id", id);
            if (!(bool)conflict.ExecuteScalar()!) break;
            title = $"{original} (восстановлено {i})";
        }
        using var command = connection.CreateCommand();
        command.CommandText = "WITH RECURSIVE branch(Id) AS (SELECT @id::bigint UNION ALL SELECT n.Id FROM Nodes n JOIN branch b ON n.ParentId=b.Id) UPDATE Nodes SET DeletedAt=NULL, ModifiedAt=@d, Title=CASE WHEN Id=@id THEN @title ELSE Title END WHERE Id IN (SELECT Id FROM branch)";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("d", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        RefreshSearchIndex(id);
    }

    public void DeletePermanently(long id)
    {
        using (var connection = Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM SearchIndex WHERE NodeId IN (WITH RECURSIVE branch(Id) AS (SELECT @id::bigint UNION ALL SELECT n.Id FROM Nodes n JOIN branch b ON n.ParentId=b.Id) SELECT Id FROM branch); DELETE FROM Nodes WHERE Id=@id AND DeletedAt IS NOT NULL";
            command.Parameters.AddWithValue("id", id);
            command.ExecuteNonQuery();
        }
        CleanupUnusedAssets();
    }

    public void ToggleFavorite(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Nodes SET IsFavorite=NOT IsFavorite WHERE Id=@id";
        command.Parameters.AddWithValue("id", id);
        command.ExecuteNonQuery();
    }

    public long Duplicate(long id)
    {
        var source = FindNode(id) ?? throw new InvalidOperationException("Документ не найден.");
        var baseTitle = source.Title + " — копия";
        var title = baseTitle;
        for (var i = 2; TitleExists(source.ParentId, title); i++) title = $"{baseTitle} ({i})";
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        long copy;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Nodes(ParentId,IsFolder,Title,SortOrder,CreatedAt,ModifiedAt,Content,PlainText,Status,EditorJson,Html,Markdown,Zoom,Guid,Author,IsPrivate,Owner)
                SELECT ParentId,IsFolder,@t,(SELECT COALESCE(MAX(SortOrder)+1,0) FROM Nodes x WHERE x.ParentId IS NOT DISTINCT FROM Nodes.ParentId),@d,@d,Content,PlainText,Status,EditorJson,Html,Markdown,Zoom,gen_random_uuid()::text,Author,IsPrivate,Owner
                FROM Nodes WHERE Id=@id
                RETURNING Id
                """;
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("t", title);
            command.Parameters.AddWithValue("d", DateTime.UtcNow.ToString("O"));
            copy = (long)command.ExecuteScalar()!;
        }
        using (var related = connection.CreateCommand())
        {
            related.Transaction = transaction;
            related.CommandText = """
                INSERT INTO Attachments(DocumentId,FileName,StoredName,MimeType,Size,Sha256,CreatedAt) SELECT @copy,FileName,StoredName,MimeType,Size,Sha256,@d FROM Attachments WHERE DocumentId=@id;
                INSERT INTO DocumentTags(DocumentId,TagId) SELECT @copy,TagId FROM DocumentTags WHERE DocumentId=@id;
                INSERT INTO CustomProperties(DocumentId,Name,Value) SELECT @copy,Name,Value FROM CustomProperties WHERE DocumentId=@id;
                """;
            related.Parameters.AddWithValue("copy", copy);
            related.Parameters.AddWithValue("id", id);
            related.Parameters.AddWithValue("d", DateTime.UtcNow.ToString("O"));
            related.ExecuteNonQuery();
        }
        transaction.Commit();
        RefreshSearchIndex(copy);
        return copy;
    }

    public void EmptyTrash()
    {
        using (var connection = Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Nodes WHERE DeletedAt IS NOT NULL AND (ParentId IS NULL OR ParentId NOT IN(SELECT Id FROM Nodes WHERE DeletedAt IS NOT NULL))";
            command.ExecuteNonQuery();
        }
        CleanupUnusedAssets();
    }

    public (FlowDocument Document, DateTime Created, DateTime Modified) LoadDocument(long id)
    {
        using var connection = Open();
        byte[]? content; string createdText, modifiedText;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT Content,CreatedAt,ModifiedAt FROM Nodes WHERE Id=@id AND {AccessFilter}";
            command.Parameters.AddWithValue("id", id);
            AddUser(command);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("Документ не найден.");
            content = reader.IsDBNull(0) ? null : (byte[])reader[0];
            createdText = reader.GetString(1); modifiedText = reader.GetString(2);
        }
        var doc = content is null ? new FlowDocument(new Paragraph(new Run(""))) : Deserialize(content);
        using (var opened = connection.CreateCommand())
        {
            opened.CommandText = "UPDATE Nodes SET LastOpenedAt=@d WHERE Id=@id";
            opened.Parameters.AddWithValue("id", id);
            opened.Parameters.AddWithValue("d", DateTime.UtcNow.ToString("O"));
            opened.ExecuteNonQuery();
        }
        return (doc, DateTime.Parse(createdText), DateTime.Parse(modifiedText));
    }

    public void SaveDocument(long id, FlowDocument document, string plainText)
    {
        var bytes = Serialize(document);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        byte[]? oldContent = null; string oldText = "";
        using (var previous = connection.CreateCommand())
        {
            previous.Transaction = transaction;
            previous.CommandText = "SELECT Content,PlainText FROM Nodes WHERE Id=@id AND Content IS NOT NULL";
            previous.Parameters.AddWithValue("id", id);
            using var reader = previous.ExecuteReader();
            if (reader.Read()) { oldContent = (byte[])reader[0]; oldText = reader.GetString(1); }
        }
        if (oldContent is not null && !oldContent.AsSpan().SequenceEqual(bytes))
        {
            using var version = connection.CreateCommand();
            version.Transaction = transaction;
            version.CommandText = "INSERT INTO DocumentVersions(DocumentId,CreatedAt,Reason,Content,PlainText,Size) VALUES(@id,@d,'Автосохранение',@c,@p,@s)";
            version.Parameters.AddWithValue("id", id);
            version.Parameters.AddWithValue("d", DateTime.UtcNow.ToString("O"));
            version.Parameters.AddWithValue("c", oldContent);
            version.Parameters.AddWithValue("p", oldText);
            version.Parameters.AddWithValue("s", oldContent.LongLength);
            version.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE Nodes SET Content=@c, PlainText=@p, ModifiedAt=@m WHERE Id=@id";
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("c", bytes);
            command.Parameters.AddWithValue("p", plainText);
            command.Parameters.AddWithValue("m", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = "DELETE FROM DocumentVersions WHERE DocumentId=@id AND IsPinned=FALSE AND Id NOT IN(SELECT Id FROM DocumentVersions WHERE DocumentId=@id AND IsPinned=FALSE ORDER BY CreatedAt DESC LIMIT 100)";
            prune.Parameters.AddWithValue("id", id);
            prune.ExecuteNonQuery();
        }
        transaction.Commit();
        RefreshSearchIndex(id);
    }

    public (string Json, string Html, string PlainText) LoadStructuredContent(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT EditorJson,Html,PlainText FROM Nodes WHERE Id=@id AND {AccessFilter}";
        command.Parameters.AddWithValue("id", id);
        AddUser(command);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Документ не найден.");
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    public void SaveStructuredContent(long id, string json, string html, string plainText)
    {
        var markdown = MarkdownService.FromHtml(html, GetTitle(id));
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "INSERT INTO DocumentVersions(DocumentId,CreatedAt,Reason,Content,PlainText,Size,EditorJson,Html) SELECT Id,@d,'Автосохранение',COALESCE(Content,''::bytea),PlainText,length(EditorJson)+length(Html),EditorJson,Html FROM Nodes WHERE Id=@id AND EditorJson<>'' AND EditorJson<>@j";
            version.Parameters.AddWithValue("id", id);
            version.Parameters.AddWithValue("d", DateTime.UtcNow.ToString("O"));
            version.Parameters.AddWithValue("j", json);
            version.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE Nodes SET EditorJson=@j, Html=@h, PlainText=@p, Markdown=@md, ModifiedAt=@d WHERE Id=@id";
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("j", json);
            command.Parameters.AddWithValue("h", html);
            command.Parameters.AddWithValue("p", plainText);
            command.Parameters.AddWithValue("md", markdown);
            command.Parameters.AddWithValue("d", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = "DELETE FROM DocumentVersions WHERE DocumentId=@id AND IsPinned=FALSE AND Id NOT IN(SELECT Id FROM DocumentVersions WHERE DocumentId=@id AND IsPinned=FALSE ORDER BY CreatedAt DESC LIMIT 100)";
            prune.Parameters.AddWithValue("id", id);
            prune.ExecuteNonQuery();
        }
        transaction.Commit();
        RefreshSearchIndex(id);
    }

    private string GetTitle(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Title FROM Nodes WHERE Id=@id";
        command.Parameters.AddWithValue("id", id);
        return command.ExecuteScalar() as string ?? "";
    }

    public string GetDocumentMarkdown(long documentId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Markdown,Html,Title FROM Nodes WHERE Id=@id AND {AccessFilter}";
        command.Parameters.AddWithValue("id", documentId);
        AddUser(command);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Документ не найден.");
        var markdown = reader.GetString(0);
        return markdown.Length > 0 ? markdown : MarkdownService.FromHtml(reader.GetString(1), reader.GetString(2));
    }

    public void SetDocumentMarkdown(long documentId, string markdown)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Nodes SET Markdown=@md, ModifiedAt=@d WHERE Id=@id";
        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("md", markdown ?? "");
        command.Parameters.AddWithValue("d", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public DocumentAccess GetDocumentAccess(long documentId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT IsPrivate,Owner FROM Nodes WHERE Id=@id";
        command.Parameters.AddWithValue("id", documentId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new DocumentAccess(documentId, reader.GetBoolean(0), reader.GetString(1)) : new DocumentAccess(documentId, false, "");
    }

    public void SetDocumentAccess(long documentId, bool isPrivate)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Nodes SET IsPrivate=@private, Owner=CASE WHEN Owner='' THEN @owner ELSE Owner END, ModifiedAt=@d WHERE Id=@id";
        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("private", isPrivate);
        command.Parameters.AddWithValue("owner", UserIdentity.Current);
        command.Parameters.AddWithValue("d", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void SaveAssetContent(string storedName, string sha256, string mimeType, byte[] data)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Assets(StoredName,Sha256,MimeType,Size,Data,CreatedAt) VALUES(@n,@h,@m,@s,@d,@c) ON CONFLICT (StoredName) DO UPDATE SET Sha256=EXCLUDED.Sha256, MimeType=EXCLUDED.MimeType, Size=EXCLUDED.Size, Data=EXCLUDED.Data";
        command.Parameters.AddWithValue("n", storedName);
        command.Parameters.AddWithValue("h", sha256);
        command.Parameters.AddWithValue("m", mimeType ?? "");
        command.Parameters.AddWithValue("s", data.LongLength);
        command.Parameters.AddWithValue("d", data);
        command.Parameters.AddWithValue("c", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public bool AssetExists(string storedName)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM Assets WHERE StoredName=@n)";
        command.Parameters.AddWithValue("n", storedName);
        return (bool)command.ExecuteScalar()!;
    }

    public (byte[] Data, string MimeType)? LoadAssetContent(string storedName)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Data,MimeType FROM Assets WHERE StoredName=@n";
        command.Parameters.AddWithValue("n", storedName);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ((byte[])reader[0], reader.GetString(1)) : null;
    }

    public IReadOnlyList<string> GetReferencedAssetNames(long documentId)
    {
        using var connection = Open();
        var names = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT EditorJson,Html,Markdown FROM Nodes WHERE Id=@id";
            command.Parameters.AddWithValue("id", documentId);
            using var reader = command.ExecuteReader();
            if (reader.Read()) names.AddRange(AssetReferences.Extract(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        using (var attachments = connection.CreateCommand())
        {
            attachments.CommandText = "SELECT StoredName FROM Attachments WHERE DocumentId=@id";
            attachments.Parameters.AddWithValue("id", documentId);
            using var reader = attachments.ExecuteReader();
            while (reader.Read()) names.Add(reader.GetString(0));
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string? GetSetting(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key=@k";
        command.Parameters.AddWithValue("k", key);
        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Settings(Key,Value) VALUES(@k,@v) ON CONFLICT(Key) DO UPDATE SET Value=@v";
        command.Parameters.AddWithValue("k", key);
        command.Parameters.AddWithValue("v", value);
        command.ExecuteNonQuery();
    }

    public void Checkpoint()
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CHECKPOINT";
            command.ExecuteNonQuery();
        }
        catch (PostgresException)
        {
            // У обычного пользователя БД может не быть прав на CHECKPOINT — это не критично, WAL Postgres обслуживается сервером самостоятельно.
        }
    }

    public long CountDocuments() => Count("IsFolder=FALSE AND DeletedAt IS NULL");
    public long CountFavorites() => Count("IsFolder=FALSE AND IsFavorite=TRUE AND DeletedAt IS NULL");
    public long CountTrash() => Count("DeletedAt IS NOT NULL");
    public long CountRecent() => Count("IsFolder=FALSE AND LastOpenedAt IS NOT NULL AND DeletedAt IS NULL");

    public string CheckIntegrity()
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.ExecuteScalar();
            return "ok";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public IReadOnlyList<DocumentVersion> GetVersions(long documentId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,DocumentId,CreatedAt,Reason,Size,IsPinned FROM DocumentVersions WHERE DocumentId=@id ORDER BY IsPinned DESC,CreatedAt DESC";
        command.Parameters.AddWithValue("id", documentId);
        var list = new List<DocumentVersion>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) list.Add(new(reader.GetInt64(0), reader.GetInt64(1), DateTime.Parse(reader.GetString(2)), reader.GetString(3), reader.GetInt64(4), reader.GetBoolean(5)));
        return list;
    }

    public string GetVersionPlainText(long versionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT PlainText FROM DocumentVersions WHERE Id=@id";
        command.Parameters.AddWithValue("id", versionId);
        return command.ExecuteScalar() as string ?? "";
    }

    public string GetDocumentPlainText(long documentId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT PlainText FROM Nodes WHERE Id=@id";
        command.Parameters.AddWithValue("id", documentId);
        return command.ExecuteScalar() as string ?? "";
    }

    public void ToggleVersionPin(long versionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE DocumentVersions SET IsPinned=NOT IsPinned WHERE Id=@id";
        command.Parameters.AddWithValue("id", versionId);
        command.ExecuteNonQuery();
    }

    public void DeleteVersion(long versionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DocumentVersions WHERE Id=@id";
        command.Parameters.AddWithValue("id", versionId);
        command.ExecuteNonQuery();
        CleanupUnusedAssets();
    }

    public void RestoreVersion(long versionId)
    {
        using var connection = Open();
        long documentId;
        using (var find = connection.CreateCommand())
        {
            find.CommandText = "SELECT DocumentId FROM DocumentVersions WHERE Id=@v";
            find.Parameters.AddWithValue("v", versionId);
            documentId = (long)(find.ExecuteScalar() ?? throw new InvalidOperationException("Версия не найдена."));
        }
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE Nodes SET Content=(SELECT Content FROM DocumentVersions WHERE Id=@v), PlainText=(SELECT PlainText FROM DocumentVersions WHERE Id=@v), EditorJson=(SELECT EditorJson FROM DocumentVersions WHERE Id=@v), Html=(SELECT Html FROM DocumentVersions WHERE Id=@v), ModifiedAt=@d WHERE Id=@id";
        command.Parameters.AddWithValue("v", versionId);
        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("d", DateTime.UtcNow.ToString("O"));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Версия не найдена.");
        transaction.Commit();
        RefreshSearchIndex(documentId);
    }

    public long RegisterAttachment(long documentId, string fileName, string storedName, string mime, long size, string sha256)
    {
        long result;
        using (var connection = Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO Attachments(DocumentId,FileName,StoredName,MimeType,Size,Sha256,CreatedAt) VALUES(@d,@f,@s,@m,@z,@h,@c) RETURNING Id";
            command.Parameters.AddWithValue("d", documentId);
            command.Parameters.AddWithValue("f", fileName);
            command.Parameters.AddWithValue("s", storedName);
            command.Parameters.AddWithValue("m", mime);
            command.Parameters.AddWithValue("z", size);
            command.Parameters.AddWithValue("h", sha256);
            command.Parameters.AddWithValue("c", DateTime.UtcNow.ToString("O"));
            result = (long)command.ExecuteScalar()!;
        }
        RefreshSearchIndex(documentId);
        return result;
    }

    public string? FindStoredAttachmentByHash(string sha256)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT StoredName FROM Attachments WHERE Sha256=@h LIMIT 1";
        command.Parameters.AddWithValue("h", sha256);
        return command.ExecuteScalar() as string;
    }

    public IReadOnlyList<AttachmentInfo> GetAttachments(long documentId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,DocumentId,FileName,StoredName,MimeType,Size,Sha256,CreatedAt FROM Attachments WHERE DocumentId=@id ORDER BY CreatedAt DESC";
        command.Parameters.AddWithValue("id", documentId);
        var list = new List<AttachmentInfo>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) list.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.GetString(6), DateTime.Parse(reader.GetString(7))));
        return list;
    }

    public (string StoredName, bool IsUnused) RemoveAttachment(long attachmentId)
    {
        string stored; long documentId;
        using (var connection = Open())
        {
            using var transaction = connection.BeginTransaction();
            using (var find = connection.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText = "SELECT StoredName,DocumentId FROM Attachments WHERE Id=@id";
                find.Parameters.AddWithValue("id", attachmentId);
                using var reader = find.ExecuteReader();
                if (!reader.Read()) throw new InvalidOperationException("Вложение не найдено.");
                stored = reader.GetString(0); documentId = reader.GetInt64(1);
            }
            using (var remove = connection.CreateCommand())
            {
                remove.Transaction = transaction;
                remove.CommandText = "DELETE FROM Attachments WHERE Id=@id";
                remove.Parameters.AddWithValue("id", attachmentId);
                remove.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        RefreshSearchIndex(documentId);
        using var check = Open();
        using var command = check.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM Attachments WHERE StoredName=@name)+(SELECT COUNT(*) FROM Nodes WHERE EditorJson LIKE @like OR Html LIKE @like)+(SELECT COUNT(*) FROM DocumentVersions WHERE EditorJson LIKE @like OR Html LIKE @like)";
        command.Parameters.AddWithValue("name", stored);
        command.Parameters.AddWithValue("like", "%" + stored + "%");
        return (stored, (long)command.ExecuteScalar()! == 0);
    }

    public void SetStatus(long id, string status)
    {
        status = status.Trim();
        using (var connection = Open())
        {
            using (var validate = connection.CreateCommand())
            {
                validate.CommandText = "SELECT EXISTS(SELECT 1 FROM Statuses WHERE Name=@s)";
                validate.Parameters.AddWithValue("s", status);
                if (!(bool)validate.ExecuteScalar()!) throw new InvalidOperationException("Неизвестный статус документа.");
            }
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Nodes SET Status=@s, ModifiedAt=@d WHERE Id=@id";
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("s", status);
            command.Parameters.AddWithValue("d", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        RefreshSearchIndex(id);
    }

    public string GetStatus(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status FROM Nodes WHERE Id=@id";
        command.Parameters.AddWithValue("id", id);
        return command.ExecuteScalar() as string ?? "Черновик";
    }

    public IReadOnlyList<string> GetStatuses()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM Statuses ORDER BY SortOrder,Name";
        var result = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public void AddStatus(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 60) throw new ArgumentException("Название статуса должно содержать от 1 до 60 символов.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Statuses(Name,SortOrder,IsBuiltIn) VALUES(@name,(SELECT COALESCE(MAX(SortOrder)+1,0) FROM Statuses),FALSE)";
        command.Parameters.AddWithValue("name", name);
        try { command.ExecuteNonQuery(); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { throw new InvalidOperationException("Такой статус уже существует.", ex); }
    }

    public void DeleteStatus(string name)
    {
        using var connection = Open();
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT IsBuiltIn,(SELECT COUNT(*) FROM Nodes WHERE Status=@name) FROM Statuses WHERE Name=@name";
            check.Parameters.AddWithValue("name", name);
            using var reader = check.ExecuteReader();
            if (!reader.Read()) return;
            if (reader.GetBoolean(0)) throw new InvalidOperationException("Встроенный статус удалить нельзя.");
            if (reader.GetInt64(1) > 0) throw new InvalidOperationException("Статус используется документами и не может быть удалён.");
        }
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Statuses WHERE Name=@name";
        command.Parameters.AddWithValue("name", name);
        command.ExecuteNonQuery();
    }

    public DocumentMetadata GetDocumentMetadata(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT n.Id,n.Guid,n.Title,n.Status,n.Author,n.CreatedAt,n.ModifiedAt,COALESCE(p.Title,''),COALESCE(t.Title,''),n.PlainText,(SELECT COUNT(*) FROM Attachments a WHERE a.DocumentId=n.Id) FROM Nodes n LEFT JOIN Nodes p ON p.Id=n.ParentId LEFT JOIN Nodes t ON t.Id=n.TemplateSourceId WHERE n.Id=@id AND n.IsFolder=FALSE";
        command.Parameters.AddWithValue("id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Документ не найден.");
        var plain = reader.GetString(9);
        var words = Regex.Matches(plain, @"[\p{L}\p{N}_]+").Count;
        return new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), DateTime.Parse(reader.GetString(5)), DateTime.Parse(reader.GetString(6)), reader.GetString(7), reader.GetString(8), words, plain.Length, reader.GetInt32(10));
    }

    public IReadOnlyList<CustomPropertyValue> GetCustomProperties(long documentId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name,Value FROM CustomProperties WHERE DocumentId=@id ORDER BY Name";
        command.Parameters.AddWithValue("id", documentId);
        var result = new List<CustomPropertyValue>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    public void SetCustomProperties(long documentId, IEnumerable<CustomPropertyValue> properties)
    {
        var normalized = properties.Select(x => new CustomPropertyValue(x.Name.Trim(), x.Value.Trim())).Where(x => x.Name.Length > 0).GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(x => x.Last()).ToList();
        if (normalized.Any(x => x.Name.Length > 80 || x.Value.Length > 4000)) throw new ArgumentException("Название поля не должно превышать 80 символов, значение — 4000 символов.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM CustomProperties WHERE DocumentId=@id";
            clear.Parameters.AddWithValue("id", documentId);
            clear.ExecuteNonQuery();
        }
        foreach (var property in normalized)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO CustomProperties(DocumentId,Name,Value) VALUES(@id,@name,@value)";
            command.Parameters.AddWithValue("id", documentId);
            command.Parameters.AddWithValue("name", property.Name);
            command.Parameters.AddWithValue("value", property.Value);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public double GetZoom(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Zoom FROM Nodes WHERE Id=@id";
        command.Parameters.AddWithValue("id", id);
        var value = command.ExecuteScalar();
        return value is null ? 1d : Convert.ToDouble(value);
    }

    public void SetZoom(long id, double value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Nodes SET Zoom=@z WHERE Id=@id";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("z", Math.Clamp(value, .5, 2));
        command.ExecuteNonQuery();
    }

    public void SetTags(long id, IEnumerable<string> tags)
    {
        using (var connection = Open())
        {
            using var transaction = connection.BeginTransaction();
            using (var clear = connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM DocumentTags WHERE DocumentId=@id";
                clear.Parameters.AddWithValue("id", id);
                clear.ExecuteNonQuery();
            }
            foreach (var value in tags.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO Tags(Name) VALUES(@n) ON CONFLICT (Name) DO NOTHING;
                    INSERT INTO DocumentTags(DocumentId,TagId) SELECT @id, Id FROM Tags WHERE Name=@n;
                    """;
                command.Parameters.AddWithValue("id", id);
                command.Parameters.AddWithValue("n", value);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        RefreshSearchIndex(id);
    }

    public IReadOnlyList<string> GetTags(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT t.Name FROM Tags t JOIN DocumentTags d ON d.TagId=t.Id WHERE d.DocumentId=@id ORDER BY t.Name";
        command.Parameters.AddWithValue("id", id);
        var list = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }

    public IReadOnlyList<DocumentSearchResult> SearchDocuments(string? query, string? status = null, string? tag = null, bool favoritesOnly = false, bool attachmentsOnly = false, bool includeTrash = false, bool sortByDate = false, int limit = 200)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        var tsQuery = BuildTsQuery(query ?? "");
        var hasQuery = tsQuery.Length > 0;
        var where = new List<string> { "n.IsFolder=FALSE", includeTrash ? "1=1" : "n.DeletedAt IS NULL", AccessFilterAliased };
        AddUser(command);
        if (!string.IsNullOrWhiteSpace(status) && status != "Все статусы") { where.Add("n.Status=@status"); command.Parameters.AddWithValue("status", status); }
        if (!string.IsNullOrWhiteSpace(tag)) { where.Add("EXISTS(SELECT 1 FROM DocumentTags dt JOIN Tags t ON t.Id=dt.TagId WHERE dt.DocumentId=n.Id AND t.Name=@tag)"); command.Parameters.AddWithValue("tag", tag.Trim()); }
        if (favoritesOnly) where.Add("n.IsFavorite=TRUE");
        if (attachmentsOnly) where.Add("EXISTS(SELECT 1 FROM Attachments a WHERE a.DocumentId=n.Id)");
        if (hasQuery) { where.Add("s.SearchVector @@ to_tsquery('russian', @query)"); command.Parameters.AddWithValue("query", tsQuery); }
        var snippet = hasQuery ? "ts_headline('russian', n.PlainText, to_tsquery('russian', @query), 'StartSel=⟦, StopSel=⟧, MaxWords=24, MinWords=8, ShortWord=3, HighlightAll=false')" : "substr(n.PlainText,1,220)";
        var relevance = hasQuery ? "ts_rank_cd(s.SearchVector, to_tsquery('russian', @query))" : "0";
        var join = hasQuery ? "JOIN SearchIndex s ON s.NodeId=n.Id" : "";
        var order = sortByDate ? "n.ModifiedAt DESC" : hasQuery ? "Rank DESC, n.ModifiedAt DESC" : "n.ModifiedAt DESC";
        command.CommandText = $"""
            SELECT n.Id, n.Title, {snippet}, n.Status, n.ModifiedAt, n.IsFavorite, n.DeletedAt IS NOT NULL, (SELECT COUNT(*) FROM Attachments a WHERE a.DocumentId=n.Id), COALESCE(p.Title,''), {relevance} AS Rank
            FROM Nodes n {join} LEFT JOIN Nodes p ON p.Id=n.ParentId
            WHERE {string.Join(" AND ", where)}
            ORDER BY {order}
            LIMIT @limit
            """;
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 1000));
        var result = new List<DocumentSearchResult>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2), reader.GetString(3), DateTime.Parse(reader.GetString(4)), reader.GetBoolean(5), reader.GetBoolean(6), reader.GetInt32(7), reader.GetString(8)));
        return result;
    }

    // Строит выражение для to_tsquery: обычные слова получают префиксный поиск ("term:*"),
    // фразы в кавычках соединяются оператором соседства "<->". Это приближённый аналог
    // FTS5-запроса из SQLite-версии (BuildFtsQuery) — стоит перепроверить на реальном Postgres.
    private static string BuildTsQuery(string value)
    {
        var terms = new List<string>();
        foreach (Match match in Regex.Matches(value.Trim(), "\"([^\"]+)\"|([\\p{L}\\p{N}_-]+)"))
        {
            if (match.Groups[1].Success)
            {
                var words = match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(SanitizeLexeme).Where(w => w.Length > 0).ToArray();
                if (words.Length > 0) terms.Add(string.Join("<->", words));
            }
            else
            {
                var term = SanitizeLexeme(match.Groups[2].Value);
                if (term.Length > 0) terms.Add(term + ":*");
            }
        }
        return string.Join(" & ", terms);
    }

    private static string SanitizeLexeme(string term) => term.Replace("'", "").Replace(":", "").Replace("&", "").Replace("|", "").Replace("!", "");

    public void RebuildSearchIndex()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM SearchIndex;
            INSERT INTO SearchIndex(NodeId,Title,PlainText)
              SELECT n.Id, n.Title,
                n.PlainText || ' ' || n.Status
                || ' ' || COALESCE((SELECT Title FROM Nodes p WHERE p.Id=n.ParentId),'')
                || ' ' || COALESCE((SELECT string_agg(t.Name,' ') FROM DocumentTags dt JOIN Tags t ON t.Id=dt.TagId WHERE dt.DocumentId=n.Id),'')
                || ' ' || COALESCE((SELECT string_agg(a.FileName,' ') FROM Attachments a WHERE a.DocumentId=n.Id),'')
              FROM Nodes n WHERE n.IsFolder=FALSE;
            """;
        command.ExecuteNonQuery();
    }

    public void CleanupUnusedAssets()
    {
        using (var purge = Open())
        {
            using var command = purge.CreateCommand();
            command.CommandText = """
                DELETE FROM Assets a
                WHERE NOT EXISTS(SELECT 1 FROM Attachments t WHERE t.StoredName=a.StoredName)
                  AND NOT EXISTS(SELECT 1 FROM Nodes n WHERE n.EditorJson LIKE '%'||a.StoredName||'%' OR n.Html LIKE '%'||a.StoredName||'%' OR n.Markdown LIKE '%'||a.StoredName||'%')
                  AND NOT EXISTS(SELECT 1 FROM DocumentVersions v WHERE v.EditorJson LIKE '%'||a.StoredName||'%' OR v.Html LIKE '%'||a.StoredName||'%')
                """;
            command.ExecuteNonQuery();
        }
        if (!Directory.Exists(AppPaths.Assets)) return;
        foreach (var file in Directory.EnumerateFiles(AppPaths.Assets))
        {
            var name = Path.GetFileName(file);
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT (SELECT COUNT(*) FROM Attachments WHERE StoredName=@name)+(SELECT COUNT(*) FROM Nodes WHERE EditorJson LIKE @like OR Html LIKE @like)+(SELECT COUNT(*) FROM DocumentVersions WHERE EditorJson LIKE @like OR Html LIKE @like)";
            command.Parameters.AddWithValue("name", name);
            command.Parameters.AddWithValue("like", "%" + name + "%");
            if ((long)command.ExecuteScalar()! == 0)
                try { File.Delete(file); } catch (Exception ex) { LogService.Error("Не удалось удалить неиспользуемый файл " + name, ex); }
        }
    }

    private long Count(string where)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM Nodes WHERE {where} AND {AccessFilter}";
        AddUser(command);
        return (long)command.ExecuteScalar()!;
    }

    private List<NodeItem> LoadFlat(string where, string order)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Id,ParentId,IsFolder,Title,SortOrder,CreatedAt,ModifiedAt FROM Nodes WHERE {where} AND {AccessFilter} ORDER BY {order}";
        AddUser(command);
        var result = new List<NodeItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new NodeItem { Id = reader.GetInt64(0), ParentId = reader.IsDBNull(1) ? null : reader.GetInt64(1), IsFolder = reader.GetBoolean(2), Title = reader.GetString(3), SortOrder = reader.GetInt32(4), CreatedAt = DateTime.Parse(reader.GetString(5)), ModifiedAt = DateTime.Parse(reader.GetString(6)) });
        return result;
    }

    private void RefreshSearchIndex(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SearchIndex(NodeId,Title,PlainText)
              SELECT n.Id, n.Title,
                n.PlainText || ' ' || n.Status
                || ' ' || COALESCE((SELECT Title FROM Nodes p WHERE p.Id=n.ParentId),'')
                || ' ' || COALESCE((SELECT string_agg(t.Name,' ') FROM DocumentTags dt JOIN Tags t ON t.Id=dt.TagId WHERE dt.DocumentId=n.Id),'')
                || ' ' || COALESCE((SELECT string_agg(a.FileName,' ') FROM Attachments a WHERE a.DocumentId=n.Id),'')
              FROM Nodes n WHERE n.Id=@id AND n.IsFolder=FALSE
            ON CONFLICT (NodeId) DO UPDATE SET Title=EXCLUDED.Title, PlainText=EXCLUDED.PlainText;
            DELETE FROM SearchIndex WHERE NodeId=@id AND NOT EXISTS (SELECT 1 FROM Nodes n WHERE n.Id=@id AND n.IsFolder=FALSE);
            """;
        command.Parameters.AddWithValue("id", id);
        command.ExecuteNonQuery();
    }

    private void RefreshBranchSearchIndex(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM SearchIndex WHERE NodeId IN (WITH RECURSIVE branch(Id) AS (SELECT @id::bigint UNION ALL SELECT n.Id FROM Nodes n JOIN branch b ON n.ParentId=b.Id) SELECT Id FROM branch);
            INSERT INTO SearchIndex(NodeId,Title,PlainText)
              SELECT n.Id, n.Title,
                n.PlainText || ' ' || n.Status
                || ' ' || COALESCE((SELECT Title FROM Nodes p WHERE p.Id=n.ParentId),'')
                || ' ' || COALESCE((SELECT string_agg(t.Name,' ') FROM DocumentTags dt JOIN Tags t ON t.Id=dt.TagId WHERE dt.DocumentId=n.Id),'')
                || ' ' || COALESCE((SELECT string_agg(a.FileName,' ') FROM Attachments a WHERE a.DocumentId=n.Id),'')
              FROM Nodes n
              WHERE n.IsFolder=FALSE AND n.Id IN (WITH RECURSIVE branch(Id) AS (SELECT @id::bigint UNION ALL SELECT x.Id FROM Nodes x JOIN branch b ON x.ParentId=b.Id) SELECT Id FROM branch)
            ON CONFLICT (NodeId) DO UPDATE SET Title=EXCLUDED.Title, PlainText=EXCLUDED.PlainText;
            """;
        command.Parameters.AddWithValue("id", id);
        command.ExecuteNonQuery();
    }

    private static void EnsureColumn(NpgsqlConnection connection, string table, string column, string definition)
    {
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS {column} {definition}";
        alter.ExecuteNonQuery();
    }

    private static byte[] Serialize(FlowDocument document) { using var stream = new MemoryStream(); var range = new TextRange(document.ContentStart, document.ContentEnd); range.Save(stream, DataFormats.XamlPackage); return stream.ToArray(); }
    private static FlowDocument Deserialize(byte[] data) { var doc = new FlowDocument(); using var stream = new MemoryStream(data); new TextRange(doc.ContentStart, doc.ContentEnd).Load(stream, DataFormats.XamlPackage); return doc; }
}

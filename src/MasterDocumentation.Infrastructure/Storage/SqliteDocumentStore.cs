using Microsoft.Data.Sqlite;
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
/// Локальное файловое хранилище на SQLite — провайдер по умолчанию. Идентично прежнему
/// поведению <c>DatabaseService</c> (теперь этот класс — тонкая делегирующая обёртка,
/// см. <see cref="DatabaseService"/>), но позволяет явно указать путь к файлу БД, а не только
/// использовать <see cref="AppPaths.Database"/>.
/// </summary>
public sealed class SqliteDocumentStore : IDocumentStore
{
    private readonly string _databasePath;

    public SqliteDocumentStore(string? databasePath = null)
    {
        _databasePath = databasePath ?? AppPaths.Database;
    }

    // Pooling выключен: переносимая БД должна освобождаться сразу для атомарной
    // замены при восстановлении и чтения при резервном копировании.
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();

    // Приватные страницы видит только их владелец. Условие подставляется во все выборки,
    // возвращающие список или содержимое документов.
    private const string AccessFilter = "(IsPrivate=0 OR Owner='' OR Owner=$user COLLATE NOCASE)";
    private const string AccessFilterAliased = "(n.IsPrivate=0 OR n.Owner='' OR n.Owner=$user COLLATE NOCASE)";
    private static void AddUser(SqliteCommand command) => command.Parameters.AddWithValue("$user", UserIdentity.Current);

    public void Initialize()
    {
        AppPaths.Ensure();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS Nodes(
              Id INTEGER PRIMARY KEY AUTOINCREMENT, ParentId INTEGER NULL REFERENCES Nodes(Id) ON DELETE CASCADE,
              IsFolder INTEGER NOT NULL, Title TEXT NOT NULL, SortOrder INTEGER NOT NULL DEFAULT 0,
              CreatedAt TEXT NOT NULL, ModifiedAt TEXT NOT NULL, Content BLOB NULL, PlainText TEXT NOT NULL DEFAULT '');
            CREATE INDEX IF NOT EXISTS IX_Nodes_Parent ON Nodes(ParentId, SortOrder);
            CREATE TABLE IF NOT EXISTS Settings(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS SchemaMigrations(Version INTEGER PRIMARY KEY,AppliedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS DocumentVersions(Id INTEGER PRIMARY KEY AUTOINCREMENT,DocumentId INTEGER NOT NULL REFERENCES Nodes(Id) ON DELETE CASCADE,CreatedAt TEXT NOT NULL,Reason TEXT NOT NULL,Content BLOB NOT NULL,PlainText TEXT NOT NULL,Size INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_DocumentVersions_Document ON DocumentVersions(DocumentId,CreatedAt DESC);
            CREATE TABLE IF NOT EXISTS Attachments(Id INTEGER PRIMARY KEY AUTOINCREMENT,DocumentId INTEGER NOT NULL REFERENCES Nodes(Id) ON DELETE CASCADE,FileName TEXT NOT NULL,StoredName TEXT NOT NULL,MimeType TEXT NOT NULL,Size INTEGER NOT NULL,Sha256 TEXT NOT NULL,CreatedAt TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Attachments_Document ON Attachments(DocumentId,CreatedAt DESC);
            CREATE INDEX IF NOT EXISTS IX_Attachments_Hash ON Attachments(Sha256);
            CREATE TABLE IF NOT EXISTS Tags(Id INTEGER PRIMARY KEY AUTOINCREMENT,Name TEXT NOT NULL UNIQUE);
            CREATE TABLE IF NOT EXISTS DocumentTags(DocumentId INTEGER NOT NULL REFERENCES Nodes(Id) ON DELETE CASCADE,TagId INTEGER NOT NULL REFERENCES Tags(Id) ON DELETE CASCADE,PRIMARY KEY(DocumentId,TagId));
            CREATE TABLE IF NOT EXISTS CustomProperties(Id INTEGER PRIMARY KEY AUTOINCREMENT,DocumentId INTEGER NOT NULL REFERENCES Nodes(Id) ON DELETE CASCADE,Name TEXT NOT NULL,Value TEXT NOT NULL,UNIQUE(DocumentId,Name));
            CREATE TABLE IF NOT EXISTS Statuses(Name TEXT PRIMARY KEY COLLATE NOCASE,SortOrder INTEGER NOT NULL DEFAULT 0,IsBuiltIn INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS Assets(StoredName TEXT PRIMARY KEY,Sha256 TEXT NOT NULL,MimeType TEXT NOT NULL DEFAULT '',Size INTEGER NOT NULL,Data BLOB NOT NULL,CreatedAt TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Assets_Hash ON Assets(Sha256);
            INSERT OR IGNORE INTO Statuses(Name,SortOrder,IsBuiltIn) VALUES('Черновик',0,1),('В работе',1,1),('На проверке',2,1),('Завершён',3,1),('Архив',4,1);
            INSERT OR IGNORE INTO SchemaMigrations(Version,AppliedAt) VALUES(1,datetime('now'));
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "Nodes", "IsFavorite", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "Nodes", "DeletedAt", "TEXT NULL");
        EnsureColumn(connection, "Nodes", "LastOpenedAt", "TEXT NULL");
        EnsureColumn(connection, "Nodes", "Status", "TEXT NOT NULL DEFAULT 'Черновик'");
        EnsureColumn(connection, "Nodes", "EditorJson", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Nodes", "Html", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Nodes", "IsTemplate", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "Nodes", "Zoom", "REAL NOT NULL DEFAULT 1.0");
        EnsureColumn(connection, "Nodes", "Guid", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Nodes", "Author", "TEXT NOT NULL DEFAULT 'Локальный пользователь'");
        EnsureColumn(connection, "Nodes", "TemplateSourceId", "INTEGER NULL");
        EnsureColumn(connection, "Nodes", "Markdown", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Nodes", "IsPrivate", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "Nodes", "Owner", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "DocumentVersions", "EditorJson", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "DocumentVersions", "Html", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "DocumentVersions", "IsPinned", "INTEGER NOT NULL DEFAULT 0");
        using(var statuses=connection.CreateCommand()){statuses.CommandText="INSERT OR IGNORE INTO Statuses(Name,SortOrder,IsBuiltIn) SELECT DISTINCT Status,100,0 FROM Nodes WHERE trim(Status)<>''";statuses.ExecuteNonQuery();}
        using (var identities = connection.CreateCommand()) { identities.CommandText = "UPDATE Nodes SET Guid=lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',abs(random())%4+1,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6))) WHERE Guid=''; CREATE UNIQUE INDEX IF NOT EXISTS IX_Nodes_Guid ON Nodes(Guid);"; identities.ExecuteNonQuery(); }
        using (var fts = connection.CreateCommand()) { fts.CommandText = "CREATE VIRTUAL TABLE IF NOT EXISTS SearchIndex USING fts5(NodeId UNINDEXED, Title, PlainText, tokenize='unicode61'); DELETE FROM SearchIndex; INSERT INTO SearchIndex(NodeId,Title,PlainText) SELECT n.Id,n.Title,n.PlainText||' '||n.Status||' '||COALESCE((SELECT Title FROM Nodes p WHERE p.Id=n.ParentId),'')||' '||COALESCE((SELECT group_concat(t.Name,' ') FROM DocumentTags dt JOIN Tags t ON t.Id=dt.TagId WHERE dt.DocumentId=n.Id),'')||' '||COALESCE((SELECT group_concat(a.FileName,' ') FROM Attachments a WHERE a.DocumentId=n.Id),'') FROM Nodes n WHERE n.IsFolder=0;"; fts.ExecuteNonQuery(); }
        LogService.Info("Хранилище инициализировано");
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public List<NodeItem> LoadTree(string? query = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(query)
            ? $"SELECT Id,ParentId,IsFolder,Title,SortOrder,CreatedAt,ModifiedAt FROM Nodes WHERE DeletedAt IS NULL AND {AccessFilter} ORDER BY SortOrder,Title"
            : $"WITH RECURSIVE matched(Id) AS (SELECT n.Id FROM Nodes n WHERE n.DeletedAt IS NULL AND {AccessFilterAliased} AND (n.Title LIKE $q OR (n.IsFolder=0 AND n.Id IN (SELECT CAST(NodeId AS INTEGER) FROM SearchIndex WHERE SearchIndex MATCH $fts)))),visible(Id) AS (SELECT Id FROM matched UNION SELECT n.ParentId FROM Nodes n JOIN visible v ON n.Id=v.Id WHERE n.ParentId IS NOT NULL) SELECT DISTINCT n.Id,n.ParentId,n.IsFolder,n.Title,n.SortOrder,n.CreatedAt,n.ModifiedAt FROM Nodes n JOIN visible v ON v.Id=n.Id WHERE n.DeletedAt IS NULL AND {AccessFilterAliased} ORDER BY n.SortOrder,n.Title";
        AddUser(command);
        if (!string.IsNullOrWhiteSpace(query)) { var term=query.Trim();command.Parameters.AddWithValue("$q", $"%{term}%"); command.Parameters.AddWithValue("$fts", '"' + term.Replace("\"", "\"\"") + "\"*"); }
        var all = new List<NodeItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) all.Add(new NodeItem { Id = reader.GetInt64(0), ParentId = reader.IsDBNull(1) ? null : reader.GetInt64(1), IsFolder = reader.GetBoolean(2), Title = reader.GetString(3), SortOrder = reader.GetInt32(4), CreatedAt = DateTime.Parse(reader.GetString(5)), ModifiedAt = DateTime.Parse(reader.GetString(6)) });
        var byId = all.ToDictionary(x => x.Id);
        foreach (var node in all.Where(x => x.ParentId.HasValue && byId.ContainsKey(x.ParentId.Value))) byId[node.ParentId!.Value].Children.Add(node);
        return all.Where(x => !x.ParentId.HasValue || !byId.ContainsKey(x.ParentId.Value)).ToList();
    }
    public List<NodeItem> LoadFavorites() => LoadFlat("IsFolder=0 AND IsFavorite=1 AND DeletedAt IS NULL", "ModifiedAt DESC");
    public List<NodeItem> LoadRecent(int limit = 25) => LoadFlat("IsFolder=0 AND LastOpenedAt IS NOT NULL AND DeletedAt IS NULL", $"LastOpenedAt DESC LIMIT {Math.Clamp(limit, 1, 100)}");
    public List<NodeItem> LoadTrash() => LoadFlat("DeletedAt IS NOT NULL", "DeletedAt DESC");
    public List<NodeItem> LoadTemplates() => LoadFlat("IsFolder=0 AND IsTemplate=1 AND DeletedAt IS NULL", "Title");
    public NodeItem? FindNode(long id) { using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText=$"SELECT Id,ParentId,IsFolder,Title,SortOrder,CreatedAt,ModifiedAt FROM Nodes WHERE Id=$id AND DeletedAt IS NULL AND {AccessFilter}"; cmd.Parameters.AddWithValue("$id",id); AddUser(cmd); using var r=cmd.ExecuteReader(); return r.Read()?new NodeItem{Id=r.GetInt64(0),ParentId=r.IsDBNull(1)?null:r.GetInt64(1),IsFolder=r.GetBoolean(2),Title=r.GetString(3),SortOrder=r.GetInt32(4),CreatedAt=DateTime.Parse(r.GetString(5)),ModifiedAt=DateTime.Parse(r.GetString(6))}:null; }

    public long Create(long? parentId, bool folder, string title) => Create(parentId, folder, title, false);

    public long Create(long? parentId, bool folder, string title, bool isPrivate)
    {
        title=title.Trim();if(title.Length==0)throw new ArgumentException("Название не может быть пустым.");if(TitleExists(parentId,title))throw new InvalidOperationException($"В выбранной папке уже существует элемент «{title}».");
        var now = DateTime.UtcNow.ToString("O");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Nodes(ParentId,IsFolder,Title,SortOrder,CreatedAt,ModifiedAt,Guid,Author,IsPrivate,Owner) VALUES($p,$f,$t,(SELECT COALESCE(MAX(SortOrder)+1,0) FROM Nodes WHERE ParentId IS $p),$c,$c,$g,$owner,$private,$owner); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$p", parentId is null ? DBNull.Value : parentId);
        command.Parameters.AddWithValue("$f", folder);
        command.Parameters.AddWithValue("$t", title);
        command.Parameters.AddWithValue("$c", now);
        command.Parameters.AddWithValue("$g", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$private", isPrivate);
        command.Parameters.AddWithValue("$owner", UserIdentity.Current);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public bool TitleExists(long? parentId,string title,long? exceptId=null){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT EXISTS(SELECT 1 FROM Nodes WHERE ParentId IS $p AND Title=$t COLLATE NOCASE AND DeletedAt IS NULL AND ($e IS NULL OR Id<>$e))";cmd.Parameters.AddWithValue("$p",parentId is null?DBNull.Value:parentId);cmd.Parameters.AddWithValue("$t",title.Trim());cmd.Parameters.AddWithValue("$e",exceptId is null?DBNull.Value:exceptId);return Convert.ToInt64(cmd.ExecuteScalar())!=0;}
    public void Rename(long id, string title) { var node=FindNode(id)??throw new InvalidOperationException("Элемент не найден.");if(TitleExists(node.ParentId,title,id))throw new InvalidOperationException($"В выбранной папке уже существует элемент «{title}».");Execute("UPDATE Nodes SET Title=$v,ModifiedAt=$m WHERE Id=$id", id, title.Trim()); if(node.IsFolder)RefreshBranchSearchIndex(id);else RefreshSearchIndex(id); }
    public void SetTemplate(long id,bool value){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Nodes SET IsTemplate=$v,ModifiedAt=$d WHERE Id=$id AND IsFolder=0";cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$v",value);cmd.Parameters.AddWithValue("$d",DateTime.UtcNow.ToString("O"));cmd.ExecuteNonQuery();}
    public long CreateFromTemplate(long templateId,long? parentId,string title,IReadOnlyDictionary<string,string>? variables=null)
    {
        if(TitleExists(parentId,title))throw new InvalidOperationException($"В выбранной папке уже существует документ «{title}».");
        using var c=Open();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="INSERT INTO Nodes(ParentId,IsFolder,Title,SortOrder,CreatedAt,ModifiedAt,Content,PlainText,Status,EditorJson,Html,Markdown,TemplateSourceId,Guid,Author,Owner) SELECT $p,0,$t,(SELECT COALESCE(MAX(SortOrder)+1,0) FROM Nodes WHERE ParentId IS $p),$d,$d,Content,PlainText,'Черновик',EditorJson,Html,Markdown,Id,lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',abs(random())%4+1,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6))),$owner,$owner FROM Nodes WHERE Id=$id AND IsTemplate=1;SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$p",parentId is null?DBNull.Value:parentId);cmd.Parameters.AddWithValue("$t",title.Trim());cmd.Parameters.AddWithValue("$d",DateTime.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$id",templateId);cmd.Parameters.AddWithValue("$owner",UserIdentity.Current);var created=Convert.ToInt64(cmd.ExecuteScalar());if(created==0)throw new InvalidOperationException("Шаблон не найден.");
        using(var copy=c.CreateCommand()){copy.Transaction=tx;copy.CommandText="INSERT INTO Attachments(DocumentId,FileName,StoredName,MimeType,Size,Sha256,CreatedAt) SELECT $new,FileName,StoredName,MimeType,Size,Sha256,$d FROM Attachments WHERE DocumentId=$source;INSERT INTO DocumentTags(DocumentId,TagId) SELECT $new,TagId FROM DocumentTags WHERE DocumentId=$source;";copy.Parameters.AddWithValue("$new",created);copy.Parameters.AddWithValue("$source",templateId);copy.Parameters.AddWithValue("$d",DateTime.UtcNow.ToString("O"));copy.ExecuteNonQuery();}
        ApplyTemplateVariables(c,tx,created,parentId,title,variables);tx.Commit();RefreshSearchIndex(created);return created;
    }
    public IReadOnlyList<string> GetTemplateVariables(long templateId)
    {
        using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT EditorJson||char(10)||Html||char(10)||PlainText FROM Nodes WHERE Id=$id AND IsTemplate=1";cmd.Parameters.AddWithValue("$id",templateId);var source=Convert.ToString(cmd.ExecuteScalar())??throw new InvalidOperationException("Шаблон не найден.");return Regex.Matches(source,@"\{\{\s*([\p{L}\p{N}_ -]{1,80})\s*\}\}").Select(x=>x.Groups[1].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x,StringComparer.CurrentCultureIgnoreCase).ToList();
    }
    private static void ApplyTemplateVariables(SqliteConnection connection,SqliteTransaction transaction,long documentId,long? parentId,string title,IReadOnlyDictionary<string,string>? custom)
    {
        var now=DateTime.Now;var values=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"Title",title},{"Date",now.ToString("d")},{"Time",now.ToString("t")},{"Author","Локальный пользователь"},{"Section",""}};if(parentId is long parent){using var parentCommand=connection.CreateCommand();parentCommand.Transaction=transaction;parentCommand.CommandText="SELECT Title FROM Nodes WHERE Id=$id";parentCommand.Parameters.AddWithValue("$id",parent);values["Section"]=Convert.ToString(parentCommand.ExecuteScalar())??"";}if(custom is not null)foreach(var item in custom)values[item.Key]=item.Value;
        using var read=connection.CreateCommand();read.Transaction=transaction;read.CommandText="SELECT EditorJson,Html,PlainText FROM Nodes WHERE Id=$id";read.Parameters.AddWithValue("$id",documentId);using var reader=read.ExecuteReader();if(!reader.Read())return;var json=reader.GetString(0);var html=reader.GetString(1);var plain=reader.GetString(2);reader.Close();
        string Replace(string input,Func<string,string> encode)=>Regex.Replace(input,@"\{\{\s*([\p{L}\p{N}_ -]{1,80})\s*\}\}",match=>values.TryGetValue(match.Groups[1].Value.Trim(),out var value)?encode(value):match.Value);
        using var update=connection.CreateCommand();update.Transaction=transaction;update.CommandText="UPDATE Nodes SET EditorJson=$j,Html=$h,PlainText=$p WHERE Id=$id";update.Parameters.AddWithValue("$id",documentId);update.Parameters.AddWithValue("$j",Replace(json,value=>JsonSerializer.Serialize(value)[1..^1]));update.Parameters.AddWithValue("$h",Replace(html,System.Net.WebUtility.HtmlEncode));update.Parameters.AddWithValue("$p",Replace(plain,value=>value));update.ExecuteNonQuery();
    }
    public string GetDocumentGuid(long id){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Guid FROM Nodes WHERE Id=$id AND IsFolder=0";cmd.Parameters.AddWithValue("$id",id);return Convert.ToString(cmd.ExecuteScalar())??throw new InvalidOperationException("Документ не найден.");}
    public NodeItem? FindDocumentByGuid(string guid){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText=$"SELECT Id,ParentId,IsFolder,Title,SortOrder,CreatedAt,ModifiedAt FROM Nodes WHERE Guid=$guid AND IsFolder=0 AND DeletedAt IS NULL AND {AccessFilter}";cmd.Parameters.AddWithValue("$guid",guid);AddUser(cmd);using var r=cmd.ExecuteReader();return r.Read()?new NodeItem{Id=r.GetInt64(0),ParentId=r.IsDBNull(1)?null:r.GetInt64(1),IsFolder=false,Title=r.GetString(3),SortOrder=r.GetInt32(4),CreatedAt=DateTime.Parse(r.GetString(5)),ModifiedAt=DateTime.Parse(r.GetString(6))}:null;}
    public void Move(long id, long? parentId)
    {
        var source=FindNode(id)??throw new InvalidOperationException("Перемещаемый элемент не найден.");if(parentId==id)throw new InvalidOperationException("Нельзя переместить элемент внутрь самого себя.");
        using var connection=Open();
        if(parentId is long parent)
        {
            using var target=connection.CreateCommand();target.CommandText="SELECT IsFolder FROM Nodes WHERE Id=$id AND DeletedAt IS NULL";target.Parameters.AddWithValue("$id",parent);var isFolder=target.ExecuteScalar();if(isFolder is null||Convert.ToInt32(isFolder)==0)throw new InvalidOperationException("Целевой элемент не является папкой.");
            using var cycle=connection.CreateCommand();cycle.CommandText="WITH RECURSIVE branch(Id) AS (SELECT $id UNION ALL SELECT n.Id FROM Nodes n JOIN branch b ON n.ParentId=b.Id) SELECT EXISTS(SELECT 1 FROM branch WHERE Id=$parent)";cycle.Parameters.AddWithValue("$id",id);cycle.Parameters.AddWithValue("$parent",parent);if(Convert.ToInt64(cycle.ExecuteScalar())!=0)throw new InvalidOperationException("Нельзя переместить папку внутрь её дочернего элемента.");
        }
        if(TitleExists(parentId,source.Title,id))throw new InvalidOperationException($"В целевой папке уже существует элемент «{source.Title}».");
        using var command=connection.CreateCommand();command.CommandText="UPDATE Nodes SET ParentId=$p,SortOrder=(SELECT COALESCE(MAX(SortOrder)+1,0) FROM Nodes WHERE ParentId IS $p AND Id<>$id),ModifiedAt=$m WHERE Id=$id";command.Parameters.AddWithValue("$id",id);command.Parameters.AddWithValue("$p",parentId is null?DBNull.Value:parentId);command.Parameters.AddWithValue("$m",DateTime.UtcNow.ToString("O"));command.ExecuteNonQuery();if(source.IsFolder)RefreshBranchSearchIndex(id);else RefreshSearchIndex(id);
    }
    public void Delete(long id) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "WITH RECURSIVE branch(Id) AS (SELECT $id UNION ALL SELECT n.Id FROM Nodes n JOIN branch b ON n.ParentId=b.Id) UPDATE Nodes SET DeletedAt=$d,ModifiedAt=$d WHERE Id IN branch"; cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$d", DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery(); }
    public void Restore(long id) { using var c=Open();long? parent;string original;using(var find=c.CreateCommand()){find.CommandText="SELECT ParentId,Title FROM Nodes WHERE Id=$id AND DeletedAt IS NOT NULL";find.Parameters.AddWithValue("$id",id);using var reader=find.ExecuteReader();if(!reader.Read())throw new InvalidOperationException("Элемент не найден в корзине.");parent=reader.IsDBNull(0)?null:reader.GetInt64(0);original=reader.GetString(1);}var title=original;for(var i=2;;i++){using var conflict=c.CreateCommand();conflict.CommandText="SELECT EXISTS(SELECT 1 FROM Nodes WHERE ParentId IS $p AND Title=$t COLLATE NOCASE AND DeletedAt IS NULL AND Id<>$id)";conflict.Parameters.AddWithValue("$p",parent is null?DBNull.Value:parent);conflict.Parameters.AddWithValue("$t",title);conflict.Parameters.AddWithValue("$id",id);if(Convert.ToInt64(conflict.ExecuteScalar())==0)break;title=$"{original} (восстановлено {i})";}using var cmd=c.CreateCommand();cmd.CommandText="WITH RECURSIVE branch(Id) AS (SELECT $id UNION ALL SELECT n.Id FROM Nodes n JOIN branch b ON n.ParentId=b.Id) UPDATE Nodes SET DeletedAt=NULL,ModifiedAt=$d,Title=CASE WHEN Id=$id THEN $title ELSE Title END WHERE Id IN branch";cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$title",title);cmd.Parameters.AddWithValue("$d",DateTime.UtcNow.ToString("O"));cmd.ExecuteNonQuery();RefreshSearchIndex(id); }
    public void DeletePermanently(long id) { using(var c = Open()){using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM SearchIndex WHERE CAST(NodeId AS INTEGER) IN (WITH RECURSIVE branch(Id) AS (SELECT $id UNION ALL SELECT n.Id FROM Nodes n JOIN branch b ON n.ParentId=b.Id) SELECT Id FROM branch); DELETE FROM Nodes WHERE Id=$id AND DeletedAt IS NOT NULL"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();} CleanupUnusedAssets(); }
    public void ToggleFavorite(long id) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE Nodes SET IsFavorite=CASE IsFavorite WHEN 0 THEN 1 ELSE 0 END WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); }
    public long Duplicate(long id) {var source=FindNode(id)??throw new InvalidOperationException("Документ не найден.");var baseTitle=source.Title+" — копия";var title=baseTitle;for(var i=2;TitleExists(source.ParentId,title);i++)title=$"{baseTitle} ({i})";using var c=Open();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO Nodes(ParentId,IsFolder,Title,SortOrder,CreatedAt,ModifiedAt,Content,PlainText,Status,EditorJson,Html,Markdown,Zoom,Guid,Author,IsPrivate,Owner) SELECT ParentId,IsFolder,$t,(SELECT COALESCE(MAX(SortOrder)+1,0) FROM Nodes x WHERE x.ParentId IS Nodes.ParentId),$d,$d,Content,PlainText,Status,EditorJson,Html,Markdown,Zoom,lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-'||substr('89ab',abs(random())%4+1,1)||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6))),Author,IsPrivate,Owner FROM Nodes WHERE Id=$id;SELECT last_insert_rowid();";cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$t",title);cmd.Parameters.AddWithValue("$d",DateTime.UtcNow.ToString("O"));var copy=Convert.ToInt64(cmd.ExecuteScalar());using(var related=c.CreateCommand()){related.Transaction=tx;related.CommandText="INSERT INTO Attachments(DocumentId,FileName,StoredName,MimeType,Size,Sha256,CreatedAt) SELECT $copy,FileName,StoredName,MimeType,Size,Sha256,$d FROM Attachments WHERE DocumentId=$id;INSERT INTO DocumentTags(DocumentId,TagId) SELECT $copy,TagId FROM DocumentTags WHERE DocumentId=$id;INSERT INTO CustomProperties(DocumentId,Name,Value) SELECT $copy,Name,Value FROM CustomProperties WHERE DocumentId=$id;";related.Parameters.AddWithValue("$copy",copy);related.Parameters.AddWithValue("$id",id);related.Parameters.AddWithValue("$d",DateTime.UtcNow.ToString("O"));related.ExecuteNonQuery();}tx.Commit();RefreshSearchIndex(copy);return copy;}
    public void EmptyTrash(){using(var c=Open()){using var cmd=c.CreateCommand();cmd.CommandText="DELETE FROM Nodes WHERE DeletedAt IS NOT NULL AND (ParentId IS NULL OR ParentId NOT IN(SELECT Id FROM Nodes WHERE DeletedAt IS NOT NULL))";cmd.ExecuteNonQuery();}CleanupUnusedAssets();}
    public int PurgeExpiredTrash(int retentionDays)
    {
        if(retentionDays<=0)return 0;
        var border=DateTime.UtcNow.AddDays(-retentionDays).ToString("O");
        int removed;
        using(var c=Open())
        {
            using var cmd=c.CreateCommand();
            // Удаляются только корни удалённых веток: вложенные узлы уходят вместе с родителем.
            cmd.CommandText="DELETE FROM Nodes WHERE DeletedAt IS NOT NULL AND DeletedAt<$border AND (ParentId IS NULL OR ParentId NOT IN(SELECT Id FROM Nodes WHERE DeletedAt IS NOT NULL))";
            cmd.Parameters.AddWithValue("$border",border);
            removed=cmd.ExecuteNonQuery();
        }
        if(removed>0)CleanupUnusedAssets();
        return removed;
    }

    public (FlowDocument Document, DateTime Created, DateTime Modified) LoadDocument(long id)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Content,CreatedAt,ModifiedAt FROM Nodes WHERE Id=$id AND {AccessFilter}"; command.Parameters.AddWithValue("$id", id); AddUser(command);
        using var reader = command.ExecuteReader(); if (!reader.Read()) throw new InvalidOperationException("Документ не найден.");
        var doc = reader.IsDBNull(0) ? new FlowDocument(new Paragraph(new Run(""))) : Deserialize((byte[])reader[0]); var created = DateTime.Parse(reader.GetString(1)); var modified = DateTime.Parse(reader.GetString(2)); reader.Close();
        using var opened = connection.CreateCommand(); opened.CommandText = "UPDATE Nodes SET LastOpenedAt=$d WHERE Id=$id"; opened.Parameters.AddWithValue("$id", id); opened.Parameters.AddWithValue("$d", DateTime.UtcNow.ToString("O")); opened.ExecuteNonQuery();
        return (doc, created, modified);
    }

    public void SaveDocument(long id, FlowDocument document, string plainText)
    {
        var bytes = Serialize(document);
        using var connection = Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand();
        byte[]? oldContent=null; string oldText=""; using (var previous = connection.CreateCommand()) { previous.Transaction=transaction; previous.CommandText="SELECT Content,PlainText FROM Nodes WHERE Id=$id AND Content IS NOT NULL"; previous.Parameters.AddWithValue("$id",id); using var reader=previous.ExecuteReader(); if(reader.Read()){oldContent=(byte[])reader[0];oldText=reader.GetString(1);} }
        if(oldContent is not null&&!oldContent.AsSpan().SequenceEqual(bytes)){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="INSERT INTO DocumentVersions(DocumentId,CreatedAt,Reason,Content,PlainText,Size) VALUES($id,$d,'Автосохранение',$c,$p,$s)";version.Parameters.AddWithValue("$id",id);version.Parameters.AddWithValue("$d",DateTime.UtcNow.ToString("O"));version.Parameters.AddWithValue("$c",oldContent);version.Parameters.AddWithValue("$p",oldText);version.Parameters.AddWithValue("$s",oldContent.LongLength);version.ExecuteNonQuery();}
        command.Transaction = transaction; command.CommandText = "UPDATE Nodes SET Content=$c,PlainText=$p,ModifiedAt=$m WHERE Id=$id";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$c", bytes); command.Parameters.AddWithValue("$p", plainText); command.Parameters.AddWithValue("$m", DateTime.UtcNow.ToString("O")); command.ExecuteNonQuery();
        using var index = connection.CreateCommand(); index.Transaction = transaction; index.CommandText = "DELETE FROM SearchIndex WHERE CAST(NodeId AS INTEGER)=$id; INSERT INTO SearchIndex(NodeId,Title,PlainText) SELECT Id,Title,PlainText FROM Nodes WHERE Id=$id; DELETE FROM DocumentVersions WHERE DocumentId=$id AND IsPinned=0 AND Id NOT IN(SELECT Id FROM DocumentVersions WHERE DocumentId=$id AND IsPinned=0 ORDER BY CreatedAt DESC LIMIT 100)"; index.Parameters.AddWithValue("$id", id); index.ExecuteNonQuery(); transaction.Commit();RefreshSearchIndex(id);
    }
    public (string Json,string Html,string PlainText) LoadStructuredContent(long id){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText=$"SELECT EditorJson,Html,PlainText FROM Nodes WHERE Id=$id AND {AccessFilter}";cmd.Parameters.AddWithValue("$id",id);AddUser(cmd);using var r=cmd.ExecuteReader();if(!r.Read())throw new InvalidOperationException("Документ не найден.");return(r.GetString(0),r.GetString(1),r.GetString(2));}
    public void SaveStructuredContent(long id,string json,string html,string plainText){var markdown=MarkdownService.FromHtml(html,GetTitle(id));using var c=Open();using var tx=c.BeginTransaction();using(var version=c.CreateCommand()){version.Transaction=tx;version.CommandText="INSERT INTO DocumentVersions(DocumentId,CreatedAt,Reason,Content,PlainText,Size,EditorJson,Html) SELECT Id,$d,'Автосохранение',COALESCE(Content,X''),PlainText,length(EditorJson)+length(Html),EditorJson,Html FROM Nodes WHERE Id=$id AND EditorJson<>'' AND EditorJson<>$j";version.Parameters.AddWithValue("$id",id);version.Parameters.AddWithValue("$d",DateTime.UtcNow.ToString("O"));version.Parameters.AddWithValue("$j",json);version.ExecuteNonQuery();}using(var cmd=c.CreateCommand()){cmd.Transaction=tx;cmd.CommandText="UPDATE Nodes SET EditorJson=$j,Html=$h,PlainText=$p,Markdown=$md,ModifiedAt=$d WHERE Id=$id;DELETE FROM SearchIndex WHERE CAST(NodeId AS INTEGER)=$id;INSERT INTO SearchIndex(NodeId,Title,PlainText) SELECT Id,Title,PlainText FROM Nodes WHERE Id=$id;DELETE FROM DocumentVersions WHERE DocumentId=$id AND IsPinned=0 AND Id NOT IN(SELECT Id FROM DocumentVersions WHERE DocumentId=$id AND IsPinned=0 ORDER BY CreatedAt DESC LIMIT 100)";cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$j",json);cmd.Parameters.AddWithValue("$h",html);cmd.Parameters.AddWithValue("$p",plainText);cmd.Parameters.AddWithValue("$md",markdown);cmd.Parameters.AddWithValue("$d",DateTime.UtcNow.ToString("O"));cmd.ExecuteNonQuery();}tx.Commit();RefreshSearchIndex(id);}
    private string GetTitle(long id){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Title FROM Nodes WHERE Id=$id";cmd.Parameters.AddWithValue("$id",id);return Convert.ToString(cmd.ExecuteScalar())??"";}
    public string GetDocumentMarkdown(long documentId){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText=$"SELECT Markdown,Html,Title FROM Nodes WHERE Id=$id AND {AccessFilter}";cmd.Parameters.AddWithValue("$id",documentId);AddUser(cmd);using var r=cmd.ExecuteReader();if(!r.Read())throw new InvalidOperationException("Документ не найден.");var markdown=r.GetString(0);return markdown.Length>0?markdown:MarkdownService.FromHtml(r.GetString(1),r.GetString(2));}
    public void SetDocumentMarkdown(long documentId,string markdown){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Nodes SET Markdown=$md,ModifiedAt=$d WHERE Id=$id";cmd.Parameters.AddWithValue("$id",documentId);cmd.Parameters.AddWithValue("$md",markdown??"");cmd.Parameters.AddWithValue("$d",DateTime.UtcNow.ToString("O"));cmd.ExecuteNonQuery();}
    public DocumentAccess GetDocumentAccess(long documentId){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT IsPrivate,Owner FROM Nodes WHERE Id=$id";cmd.Parameters.AddWithValue("$id",documentId);using var r=cmd.ExecuteReader();return r.Read()?new DocumentAccess(documentId,r.GetBoolean(0),r.GetString(1)):new DocumentAccess(documentId,false,"");}
    public void SetDocumentAccess(long documentId,bool isPrivate){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Nodes SET IsPrivate=$private,Owner=CASE WHEN Owner='' THEN $owner ELSE Owner END,ModifiedAt=$d WHERE Id=$id";cmd.Parameters.AddWithValue("$id",documentId);cmd.Parameters.AddWithValue("$private",isPrivate);cmd.Parameters.AddWithValue("$owner",UserIdentity.Current);cmd.Parameters.AddWithValue("$d",DateTime.UtcNow.ToString("O"));cmd.ExecuteNonQuery();}
    public void SaveAssetContent(string storedName,string sha256,string mimeType,byte[] data){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO Assets(StoredName,Sha256,MimeType,Size,Data,CreatedAt) VALUES($n,$h,$m,$s,$d,$c) ON CONFLICT(StoredName) DO UPDATE SET Sha256=$h,MimeType=$m,Size=$s,Data=$d";cmd.Parameters.AddWithValue("$n",storedName);cmd.Parameters.AddWithValue("$h",sha256);cmd.Parameters.AddWithValue("$m",mimeType??"");cmd.Parameters.AddWithValue("$s",data.LongLength);cmd.Parameters.AddWithValue("$d",data);cmd.Parameters.AddWithValue("$c",DateTime.UtcNow.ToString("O"));cmd.ExecuteNonQuery();}
    public bool AssetExists(string storedName){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT EXISTS(SELECT 1 FROM Assets WHERE StoredName=$n)";cmd.Parameters.AddWithValue("$n",storedName);return Convert.ToInt64(cmd.ExecuteScalar())!=0;}
    public (byte[] Data,string MimeType)? LoadAssetContent(string storedName){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Data,MimeType FROM Assets WHERE StoredName=$n";cmd.Parameters.AddWithValue("$n",storedName);using var r=cmd.ExecuteReader();return r.Read()?((byte[])r[0],r.GetString(1)):null;}
    public IReadOnlyList<string> GetReferencedAssetNames(long documentId){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT EditorJson,Html,Markdown FROM Nodes WHERE Id=$id";cmd.Parameters.AddWithValue("$id",documentId);var names=new List<string>();using(var r=cmd.ExecuteReader())if(r.Read())names.AddRange(AssetReferences.Extract(r.GetString(0),r.GetString(1),r.GetString(2)));using var attachments=c.CreateCommand();attachments.CommandText="SELECT StoredName FROM Attachments WHERE DocumentId=$id";attachments.Parameters.AddWithValue("$id",documentId);using var reader=attachments.ExecuteReader();while(reader.Read())names.Add(reader.GetString(0));return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();}

    public string? GetSetting(string key) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Value FROM Settings WHERE Key=$k"; cmd.Parameters.AddWithValue("$k", key); return cmd.ExecuteScalar() as string; }
    public void SetSetting(string key, string value) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT INTO Settings(Key,Value) VALUES($k,$v) ON CONFLICT(Key) DO UPDATE SET Value=$v"; cmd.Parameters.AddWithValue("$k", key); cmd.Parameters.AddWithValue("$v", value); cmd.ExecuteNonQuery(); }
    public void Checkpoint() { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; cmd.ExecuteNonQuery(); }
    public long CountDocuments() => Count("IsFolder=0 AND DeletedAt IS NULL");
    public long CountFavorites() => Count("IsFolder=0 AND IsFavorite=1 AND DeletedAt IS NULL");
    public long CountTrash() => Count("DeletedAt IS NOT NULL");
    public long CountRecent() => Count("IsFolder=0 AND LastOpenedAt IS NOT NULL AND DeletedAt IS NULL");
    public string CheckIntegrity() { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA integrity_check"; return Convert.ToString(cmd.ExecuteScalar()) ?? "unknown"; }
    public IReadOnlyList<DocumentVersion> GetVersions(long documentId) { using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Id,DocumentId,CreatedAt,Reason,Size,IsPinned FROM DocumentVersions WHERE DocumentId=$id ORDER BY IsPinned DESC,CreatedAt DESC";cmd.Parameters.AddWithValue("$id",documentId);var list=new List<DocumentVersion>();using var r=cmd.ExecuteReader();while(r.Read())list.Add(new(r.GetInt64(0),r.GetInt64(1),DateTime.Parse(r.GetString(2)),r.GetString(3),r.GetInt64(4),r.GetBoolean(5)));return list; }
    public string GetVersionPlainText(long versionId){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT PlainText FROM DocumentVersions WHERE Id=$id";cmd.Parameters.AddWithValue("$id",versionId);return Convert.ToString(cmd.ExecuteScalar())??"";}
    public string GetDocumentPlainText(long documentId){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT PlainText FROM Nodes WHERE Id=$id";cmd.Parameters.AddWithValue("$id",documentId);return Convert.ToString(cmd.ExecuteScalar())??"";}
    public void ToggleVersionPin(long versionId){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE DocumentVersions SET IsPinned=CASE IsPinned WHEN 0 THEN 1 ELSE 0 END WHERE Id=$id";cmd.Parameters.AddWithValue("$id",versionId);cmd.ExecuteNonQuery();}
    public void DeleteVersion(long versionId){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="DELETE FROM DocumentVersions WHERE Id=$id";cmd.Parameters.AddWithValue("$id",versionId);cmd.ExecuteNonQuery();CleanupUnusedAssets();}
    public void RestoreVersion(long versionId) { using var c=Open();long documentId;using(var find=c.CreateCommand()){find.CommandText="SELECT DocumentId FROM DocumentVersions WHERE Id=$v";find.Parameters.AddWithValue("$v",versionId);documentId=Convert.ToInt64(find.ExecuteScalar()??throw new InvalidOperationException("Версия не найдена."));}using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="UPDATE Nodes SET Content=(SELECT Content FROM DocumentVersions WHERE Id=$v),PlainText=(SELECT PlainText FROM DocumentVersions WHERE Id=$v),EditorJson=(SELECT EditorJson FROM DocumentVersions WHERE Id=$v),Html=(SELECT Html FROM DocumentVersions WHERE Id=$v),ModifiedAt=$d WHERE Id=$id";cmd.Parameters.AddWithValue("$v",versionId);cmd.Parameters.AddWithValue("$id",documentId);cmd.Parameters.AddWithValue("$d",DateTime.UtcNow.ToString("O"));if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Версия не найдена.");tx.Commit();RefreshSearchIndex(documentId); }
    public long RegisterAttachment(long documentId,string fileName,string storedName,string mime,long size,string sha256){long result;using(var c=Open()){using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO Attachments(DocumentId,FileName,StoredName,MimeType,Size,Sha256,CreatedAt) VALUES($d,$f,$s,$m,$z,$h,$c);SELECT last_insert_rowid();";cmd.Parameters.AddWithValue("$d",documentId);cmd.Parameters.AddWithValue("$f",fileName);cmd.Parameters.AddWithValue("$s",storedName);cmd.Parameters.AddWithValue("$m",mime);cmd.Parameters.AddWithValue("$z",size);cmd.Parameters.AddWithValue("$h",sha256);cmd.Parameters.AddWithValue("$c",DateTime.UtcNow.ToString("O"));result=Convert.ToInt64(cmd.ExecuteScalar());}RefreshSearchIndex(documentId);return result;}
    public string? FindStoredAttachmentByHash(string sha256){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT StoredName FROM Attachments WHERE Sha256=$h LIMIT 1";cmd.Parameters.AddWithValue("$h",sha256);return cmd.ExecuteScalar() as string;}
    public IReadOnlyList<AttachmentInfo> GetAttachments(long documentId){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Id,DocumentId,FileName,StoredName,MimeType,Size,Sha256,CreatedAt FROM Attachments WHERE DocumentId=$id ORDER BY CreatedAt DESC";cmd.Parameters.AddWithValue("$id",documentId);var list=new List<AttachmentInfo>();using var r=cmd.ExecuteReader();while(r.Read())list.Add(new(r.GetInt64(0),r.GetInt64(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetInt64(5),r.GetString(6),DateTime.Parse(r.GetString(7))));return list;}
    public (string StoredName,bool IsUnused) RemoveAttachment(long attachmentId){string stored;long documentId;using(var c=Open()){using var tx=c.BeginTransaction();using(var find=c.CreateCommand()){find.Transaction=tx;find.CommandText="SELECT StoredName,DocumentId FROM Attachments WHERE Id=$id";find.Parameters.AddWithValue("$id",attachmentId);using var reader=find.ExecuteReader();if(!reader.Read())throw new InvalidOperationException("Вложение не найдено.");stored=reader.GetString(0);documentId=reader.GetInt64(1);}using(var remove=c.CreateCommand()){remove.Transaction=tx;remove.CommandText="DELETE FROM Attachments WHERE Id=$id";remove.Parameters.AddWithValue("$id",attachmentId);remove.ExecuteNonQuery();}tx.Commit();}RefreshSearchIndex(documentId);using var check=Open();using var cmd=check.CreateCommand();cmd.CommandText="SELECT (SELECT COUNT(*) FROM Attachments WHERE StoredName=$name)+(SELECT COUNT(*) FROM Nodes WHERE EditorJson LIKE $like OR Html LIKE $like)+(SELECT COUNT(*) FROM DocumentVersions WHERE EditorJson LIKE $like OR Html LIKE $like)";cmd.Parameters.AddWithValue("$name",stored);cmd.Parameters.AddWithValue("$like","%"+stored+"%");return(stored,Convert.ToInt64(cmd.ExecuteScalar())==0);}
    public void SetStatus(long id,string status){status=status.Trim();using(var c=Open()){using(var validate=c.CreateCommand()){validate.CommandText="SELECT EXISTS(SELECT 1 FROM Statuses WHERE Name=$s)";validate.Parameters.AddWithValue("$s",status);if(Convert.ToInt64(validate.ExecuteScalar())==0)throw new InvalidOperationException("Неизвестный статус документа.");}using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Nodes SET Status=$s,ModifiedAt=$d WHERE Id=$id";cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$s",status);cmd.Parameters.AddWithValue("$d",DateTime.UtcNow.ToString("O"));cmd.ExecuteNonQuery();}RefreshSearchIndex(id);}
    public string GetStatus(long id){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Status FROM Nodes WHERE Id=$id";cmd.Parameters.AddWithValue("$id",id);return Convert.ToString(cmd.ExecuteScalar())??"Черновик";}
    public IReadOnlyList<string> GetStatuses(){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Name FROM Statuses ORDER BY SortOrder,Name";var result=new List<string>();using var reader=cmd.ExecuteReader();while(reader.Read())result.Add(reader.GetString(0));return result;}
    public void AddStatus(string name){name=name.Trim();if(name.Length is <1 or >60)throw new ArgumentException("Название статуса должно содержать от 1 до 60 символов.");using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO Statuses(Name,SortOrder,IsBuiltIn) VALUES($name,(SELECT COALESCE(MAX(SortOrder)+1,0) FROM Statuses),0)";cmd.Parameters.AddWithValue("$name",name);try{cmd.ExecuteNonQuery();}catch(SqliteException ex) when(ex.SqliteErrorCode==19){throw new InvalidOperationException("Такой статус уже существует.",ex);}}
    public void DeleteStatus(string name){using var c=Open();using(var check=c.CreateCommand()){check.CommandText="SELECT IsBuiltIn,(SELECT COUNT(*) FROM Nodes WHERE Status=$name) FROM Statuses WHERE Name=$name";check.Parameters.AddWithValue("$name",name);using var reader=check.ExecuteReader();if(!reader.Read())return;if(reader.GetBoolean(0))throw new InvalidOperationException("Встроенный статус удалить нельзя.");if(reader.GetInt64(1)>0)throw new InvalidOperationException("Статус используется документами и не может быть удалён.");}using var cmd=c.CreateCommand();cmd.CommandText="DELETE FROM Statuses WHERE Name=$name";cmd.Parameters.AddWithValue("$name",name);cmd.ExecuteNonQuery();}
    public DocumentMetadata GetDocumentMetadata(long id){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT n.Id,n.Guid,n.Title,n.Status,n.Author,n.CreatedAt,n.ModifiedAt,COALESCE(p.Title,''),COALESCE(t.Title,''),n.PlainText,(SELECT COUNT(*) FROM Attachments a WHERE a.DocumentId=n.Id) FROM Nodes n LEFT JOIN Nodes p ON p.Id=n.ParentId LEFT JOIN Nodes t ON t.Id=n.TemplateSourceId WHERE n.Id=$id AND n.IsFolder=0";cmd.Parameters.AddWithValue("$id",id);using var reader=cmd.ExecuteReader();if(!reader.Read())throw new InvalidOperationException("Документ не найден.");var plain=reader.GetString(9);var words=Regex.Matches(plain,@"[\p{L}\p{N}_]+").Count;return new(reader.GetInt64(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),DateTime.Parse(reader.GetString(5)),DateTime.Parse(reader.GetString(6)),reader.GetString(7),reader.GetString(8),words,plain.Length,reader.GetInt32(10));}
    public IReadOnlyList<CustomPropertyValue> GetCustomProperties(long documentId){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Name,Value FROM CustomProperties WHERE DocumentId=$id ORDER BY Name";cmd.Parameters.AddWithValue("$id",documentId);var result=new List<CustomPropertyValue>();using var reader=cmd.ExecuteReader();while(reader.Read())result.Add(new(reader.GetString(0),reader.GetString(1)));return result;}
    public void SetCustomProperties(long documentId,IEnumerable<CustomPropertyValue> properties){var normalized=properties.Select(x=>new CustomPropertyValue(x.Name.Trim(),x.Value.Trim())).Where(x=>x.Name.Length>0).GroupBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).Select(x=>x.Last()).ToList();if(normalized.Any(x=>x.Name.Length>80||x.Value.Length>4000))throw new ArgumentException("Название поля не должно превышать 80 символов, значение — 4000 символов.");using var c=Open();using var tx=c.BeginTransaction();using(var clear=c.CreateCommand()){clear.Transaction=tx;clear.CommandText="DELETE FROM CustomProperties WHERE DocumentId=$id";clear.Parameters.AddWithValue("$id",documentId);clear.ExecuteNonQuery();}foreach(var property in normalized){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO CustomProperties(DocumentId,Name,Value) VALUES($id,$name,$value)";cmd.Parameters.AddWithValue("$id",documentId);cmd.Parameters.AddWithValue("$name",property.Name);cmd.Parameters.AddWithValue("$value",property.Value);cmd.ExecuteNonQuery();}tx.Commit();}
    public double GetZoom(long id){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Zoom FROM Nodes WHERE Id=$id";cmd.Parameters.AddWithValue("$id",id);return Convert.ToDouble(cmd.ExecuteScalar()??1d);}
    public void SetZoom(long id,double value){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Nodes SET Zoom=$z WHERE Id=$id";cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$z",Math.Clamp(value,.5,2));cmd.ExecuteNonQuery();}
    public void SetTags(long id,IEnumerable<string> tags){using(var c=Open()){using var tx=c.BeginTransaction();using(var clear=c.CreateCommand()){clear.Transaction=tx;clear.CommandText="DELETE FROM DocumentTags WHERE DocumentId=$id";clear.Parameters.AddWithValue("$id",id);clear.ExecuteNonQuery();}foreach(var value in tags.Select(x=>x.Trim()).Where(x=>x.Length>0).Distinct(StringComparer.OrdinalIgnoreCase)){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO Tags(Name) VALUES($n) ON CONFLICT(Name) DO NOTHING;INSERT INTO DocumentTags(DocumentId,TagId) SELECT $id,Id FROM Tags WHERE Name=$n";cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$n",value);cmd.ExecuteNonQuery();}tx.Commit();}RefreshSearchIndex(id);}
    public IReadOnlyList<string> GetTags(long id){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT t.Name FROM Tags t JOIN DocumentTags d ON d.TagId=t.Id WHERE d.DocumentId=$id ORDER BY t.Name";cmd.Parameters.AddWithValue("$id",id);var list=new List<string>();using var r=cmd.ExecuteReader();while(r.Read())list.Add(r.GetString(0));return list;}
    public IReadOnlyList<DocumentSearchResult> SearchDocuments(string? query,string? status=null,string? tag=null,bool favoritesOnly=false,bool attachmentsOnly=false,bool includeTrash=false,bool sortByDate=false,int limit=200)
    {
        using var c=Open();using var cmd=c.CreateCommand();var ftsQuery=BuildFtsQuery(query??"");var hasQuery=ftsQuery.Length>0;var where=new List<string>{"n.IsFolder=0",includeTrash?"1=1":"n.DeletedAt IS NULL",AccessFilterAliased};AddUser(cmd);
        if(!string.IsNullOrWhiteSpace(status)&&status!="Все статусы"){where.Add("n.Status=$status");cmd.Parameters.AddWithValue("$status",status);}
        if(!string.IsNullOrWhiteSpace(tag)){where.Add("EXISTS(SELECT 1 FROM DocumentTags dt JOIN Tags t ON t.Id=dt.TagId WHERE dt.DocumentId=n.Id AND t.Name=$tag COLLATE NOCASE)");cmd.Parameters.AddWithValue("$tag",tag.Trim());}
        if(favoritesOnly)where.Add("n.IsFavorite=1");if(attachmentsOnly)where.Add("EXISTS(SELECT 1 FROM Attachments a WHERE a.DocumentId=n.Id)");
        if(hasQuery){where.Add("SearchIndex MATCH $query");cmd.Parameters.AddWithValue("$query",ftsQuery);}
        var snippet=hasQuery?"snippet(SearchIndex,2,'⟦','⟧',' … ',24)":"substr(n.PlainText,1,220)";var relevance=hasQuery?"bm25(SearchIndex)":"0";var join=hasQuery?"JOIN SearchIndex ON CAST(SearchIndex.NodeId AS INTEGER)=n.Id":"";var order=sortByDate?"n.ModifiedAt DESC":hasQuery?"Rank,n.ModifiedAt DESC":"n.ModifiedAt DESC";
        cmd.CommandText=$"SELECT n.Id,n.Title,{snippet},n.Status,n.ModifiedAt,n.IsFavorite,n.DeletedAt IS NOT NULL,(SELECT COUNT(*) FROM Attachments a WHERE a.DocumentId=n.Id),COALESCE(p.Title,''),{relevance} AS Rank FROM Nodes n {join} LEFT JOIN Nodes p ON p.Id=n.ParentId WHERE {string.Join(" AND ",where)} ORDER BY {order} LIMIT $limit";cmd.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,1000));
        var result=new List<DocumentSearchResult>();using var reader=cmd.ExecuteReader();while(reader.Read())result.Add(new(reader.GetInt64(0),reader.GetString(1),reader.IsDBNull(2)?"":reader.GetString(2),reader.GetString(3),DateTime.Parse(reader.GetString(4)),reader.GetBoolean(5),reader.GetBoolean(6),reader.GetInt32(7),reader.GetString(8)));return result;
    }
    private static string BuildFtsQuery(string value)
    {
        var terms=new List<string>();foreach(Match match in Regex.Matches(value.Trim(),"\\\"([^\\\"]+)\\\"|([\\p{L}\\p{N}_-]+)")){var term=(match.Groups[1].Success?match.Groups[1].Value:match.Groups[2].Value).Replace("\"","\"\"");if(term.Length>0)terms.Add("\""+term+"\""+(match.Groups[1].Success?"":"*"));}return string.Join(" AND ",terms);
    }
    public void RebuildSearchIndex(){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="DELETE FROM SearchIndex;INSERT INTO SearchIndex(NodeId,Title,PlainText) SELECT n.Id,n.Title,n.PlainText||' '||n.Status||' '||COALESCE((SELECT Title FROM Nodes p WHERE p.Id=n.ParentId),'')||' '||COALESCE((SELECT group_concat(t.Name,' ') FROM DocumentTags dt JOIN Tags t ON t.Id=dt.TagId WHERE dt.DocumentId=n.Id),'')||' '||COALESCE((SELECT group_concat(a.FileName,' ') FROM Attachments a WHERE a.DocumentId=n.Id),'') FROM Nodes n WHERE n.IsFolder=0";cmd.ExecuteNonQuery();}
    public void CleanupUnusedAssets(){using(var purge=Open()){using var cmd=purge.CreateCommand();cmd.CommandText="DELETE FROM Assets WHERE StoredName NOT IN(SELECT StoredName FROM Attachments) AND NOT EXISTS(SELECT 1 FROM Nodes WHERE EditorJson LIKE '%'||Assets.StoredName||'%' OR Html LIKE '%'||Assets.StoredName||'%' OR Markdown LIKE '%'||Assets.StoredName||'%') AND NOT EXISTS(SELECT 1 FROM DocumentVersions WHERE EditorJson LIKE '%'||Assets.StoredName||'%' OR Html LIKE '%'||Assets.StoredName||'%')";cmd.ExecuteNonQuery();}if(!Directory.Exists(AppPaths.Assets))return;foreach(var file in Directory.EnumerateFiles(AppPaths.Assets)){var name=Path.GetFileName(file);using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT (SELECT COUNT(*) FROM Attachments WHERE StoredName=$name)+(SELECT COUNT(*) FROM Nodes WHERE EditorJson LIKE $like OR Html LIKE $like)+(SELECT COUNT(*) FROM DocumentVersions WHERE EditorJson LIKE $like OR Html LIKE $like)";cmd.Parameters.AddWithValue("$name",name);cmd.Parameters.AddWithValue("$like","%"+name+"%");if(Convert.ToInt64(cmd.ExecuteScalar())==0)try{File.Delete(file);}catch(Exception ex){LogService.Error("Не удалось удалить неиспользуемый файл "+name,ex);}}}

    private void Execute(string sql, long id, string value) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$v", value); cmd.Parameters.AddWithValue("$m", DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery(); }
    private long Count(string where) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = $"SELECT COUNT(*) FROM Nodes WHERE {where} AND {AccessFilter}"; AddUser(cmd); return Convert.ToInt64(cmd.ExecuteScalar()); }
    private List<NodeItem> LoadFlat(string where, string order) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = $"SELECT Id,ParentId,IsFolder,Title,SortOrder,CreatedAt,ModifiedAt FROM Nodes WHERE {where} AND {AccessFilter} ORDER BY {order}"; AddUser(cmd); var result = new List<NodeItem>(); using var reader = cmd.ExecuteReader(); while (reader.Read()) result.Add(new NodeItem { Id=reader.GetInt64(0), ParentId=reader.IsDBNull(1)?null:reader.GetInt64(1), IsFolder=reader.GetBoolean(2), Title=reader.GetString(3), SortOrder=reader.GetInt32(4), CreatedAt=DateTime.Parse(reader.GetString(5)), ModifiedAt=DateTime.Parse(reader.GetString(6)) }); return result; }
    private void RefreshSearchIndex(long id) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM SearchIndex WHERE CAST(NodeId AS INTEGER)=$id; INSERT INTO SearchIndex(NodeId,Title,PlainText) SELECT n.Id,n.Title,n.PlainText||' '||n.Status||' '||COALESCE((SELECT Title FROM Nodes p WHERE p.Id=n.ParentId),'')||' '||COALESCE((SELECT group_concat(t.Name,' ') FROM DocumentTags dt JOIN Tags t ON t.Id=dt.TagId WHERE dt.DocumentId=n.Id),'')||' '||COALESCE((SELECT group_concat(a.FileName,' ') FROM Attachments a WHERE a.DocumentId=n.Id),'') FROM Nodes n WHERE n.Id=$id AND n.IsFolder=0"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); }
    private void RefreshBranchSearchIndex(long id){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="DELETE FROM SearchIndex WHERE CAST(NodeId AS INTEGER) IN(WITH RECURSIVE branch(Id) AS(SELECT $id UNION ALL SELECT n.Id FROM Nodes n JOIN branch b ON n.ParentId=b.Id)SELECT Id FROM branch);INSERT INTO SearchIndex(NodeId,Title,PlainText) SELECT n.Id,n.Title,n.PlainText||' '||n.Status||' '||COALESCE((SELECT Title FROM Nodes p WHERE p.Id=n.ParentId),'')||' '||COALESCE((SELECT group_concat(t.Name,' ') FROM DocumentTags dt JOIN Tags t ON t.Id=dt.TagId WHERE dt.DocumentId=n.Id),'')||' '||COALESCE((SELECT group_concat(a.FileName,' ') FROM Attachments a WHERE a.DocumentId=n.Id),'') FROM Nodes n WHERE n.IsFolder=0 AND n.Id IN(WITH RECURSIVE branch(Id) AS(SELECT $id UNION ALL SELECT x.Id FROM Nodes x JOIN branch b ON x.ParentId=b.Id)SELECT Id FROM branch)";cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();}
    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition) { using var check = connection.CreateCommand(); check.CommandText = $"PRAGMA table_info({table})"; using var reader = check.ExecuteReader(); while (reader.Read()) if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return; reader.Close(); using var alter = connection.CreateCommand(); alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}"; alter.ExecuteNonQuery(); }
    private static byte[] Serialize(FlowDocument document) { using var stream = new MemoryStream(); var range = new TextRange(document.ContentStart, document.ContentEnd); range.Save(stream, DataFormats.XamlPackage); return stream.ToArray(); }
    private static FlowDocument Deserialize(byte[] data) { var doc = new FlowDocument(); using var stream = new MemoryStream(data); new TextRange(doc.ContentStart, doc.ContentEnd).Load(stream, DataFormats.XamlPackage); return doc; }

}

using Microsoft.Data.Sqlite;
using System.IO;
using MasterDocumentation.Models;
using MasterDocumentation.Services;
using MasterDocumentation.Utilities;
using System.Windows;
using System.Windows.Documents;

namespace MasterDocumentation.Storage;

public sealed class DatabaseService
{
    // Pooling выключен: переносимая БД должна освобождаться сразу для атомарной
    // замены при восстановлении и чтения при резервном копировании.
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = AppPaths.Database, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();

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
        EnsureColumn(connection, "DocumentVersions", "EditorJson", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "DocumentVersions", "Html", "TEXT NOT NULL DEFAULT ''");
        using (var fts = connection.CreateCommand()) { fts.CommandText = "CREATE VIRTUAL TABLE IF NOT EXISTS SearchIndex USING fts5(NodeId UNINDEXED, Title, PlainText, tokenize='unicode61'); INSERT INTO SearchIndex(NodeId,Title,PlainText) SELECT Id,Title,PlainText FROM Nodes WHERE IsFolder=0 AND Id NOT IN(SELECT CAST(NodeId AS INTEGER) FROM SearchIndex);"; fts.ExecuteNonQuery(); }
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM Nodes";
        if (Convert.ToInt64(count.ExecuteScalar()) == 0) CreateDemo(connection);
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
            ? "SELECT Id,ParentId,IsFolder,Title,SortOrder,CreatedAt,ModifiedAt FROM Nodes WHERE DeletedAt IS NULL ORDER BY SortOrder,Title"
            : "SELECT DISTINCT n.Id,n.ParentId,n.IsFolder,n.Title,n.SortOrder,n.CreatedAt,n.ModifiedAt FROM Nodes n WHERE n.DeletedAt IS NULL AND (n.IsFolder=1 OR n.Id IN (SELECT CAST(NodeId AS INTEGER) FROM SearchIndex WHERE SearchIndex MATCH $fts) OR n.Title LIKE $q) ORDER BY n.SortOrder,n.Title";
        if (!string.IsNullOrWhiteSpace(query)) { command.Parameters.AddWithValue("$q", $"%{query}%"); command.Parameters.AddWithValue("$fts", '"' + query.Replace("\"", "\"\"") + '"'); }
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
    public NodeItem? FindNode(long id) { using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText="SELECT Id,ParentId,IsFolder,Title,SortOrder,CreatedAt,ModifiedAt FROM Nodes WHERE Id=$id AND DeletedAt IS NULL"; cmd.Parameters.AddWithValue("$id",id); using var r=cmd.ExecuteReader(); return r.Read()?new NodeItem{Id=r.GetInt64(0),ParentId=r.IsDBNull(1)?null:r.GetInt64(1),IsFolder=r.GetBoolean(2),Title=r.GetString(3),SortOrder=r.GetInt32(4),CreatedAt=DateTime.Parse(r.GetString(5)),ModifiedAt=DateTime.Parse(r.GetString(6))}:null; }

    public long Create(long? parentId, bool folder, string title)
    {
        title=title.Trim();if(title.Length==0)throw new ArgumentException("Название не может быть пустым.");if(TitleExists(parentId,title))throw new InvalidOperationException($"В выбранной папке уже существует элемент «{title}».");
        var now = DateTime.UtcNow.ToString("O");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Nodes(ParentId,IsFolder,Title,SortOrder,CreatedAt,ModifiedAt) VALUES($p,$f,$t,(SELECT COALESCE(MAX(SortOrder)+1,0) FROM Nodes WHERE ParentId IS $p),$c,$c); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$p", parentId is null ? DBNull.Value : parentId);
        command.Parameters.AddWithValue("$f", folder);
        command.Parameters.AddWithValue("$t", title);
        command.Parameters.AddWithValue("$c", now);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public bool TitleExists(long? parentId,string title,long? exceptId=null){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT EXISTS(SELECT 1 FROM Nodes WHERE ParentId IS $p AND Title=$t COLLATE NOCASE AND DeletedAt IS NULL AND ($e IS NULL OR Id<>$e))";cmd.Parameters.AddWithValue("$p",parentId is null?DBNull.Value:parentId);cmd.Parameters.AddWithValue("$t",title.Trim());cmd.Parameters.AddWithValue("$e",exceptId is null?DBNull.Value:exceptId);return Convert.ToInt64(cmd.ExecuteScalar())!=0;}
    public void Rename(long id, string title) { var node=FindNode(id)??throw new InvalidOperationException("Элемент не найден.");if(TitleExists(node.ParentId,title,id))throw new InvalidOperationException($"В выбранной папке уже существует элемент «{title}».");Execute("UPDATE Nodes SET Title=$v,ModifiedAt=$m WHERE Id=$id", id, title.Trim()); RefreshSearchIndex(id); }
    public void SetTemplate(long id,bool value){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Nodes SET IsTemplate=$v,ModifiedAt=$d WHERE Id=$id AND IsFolder=0";cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$v",value);cmd.Parameters.AddWithValue("$d",DateTime.UtcNow.ToString("O"));cmd.ExecuteNonQuery();}
    public long CreateFromTemplate(long templateId,long? parentId,string title){if(TitleExists(parentId,title))throw new InvalidOperationException($"В выбранной папке уже существует документ «{title}».");using var c=Open();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO Nodes(ParentId,IsFolder,Title,SortOrder,CreatedAt,ModifiedAt,Content,PlainText,Status,EditorJson,Html) SELECT $p,0,$t,(SELECT COALESCE(MAX(SortOrder)+1,0) FROM Nodes WHERE ParentId IS $p),$d,$d,Content,PlainText,'Черновик',EditorJson,Html FROM Nodes WHERE Id=$id AND IsTemplate=1;SELECT last_insert_rowid();";cmd.Parameters.AddWithValue("$p",parentId is null?DBNull.Value:parentId);cmd.Parameters.AddWithValue("$t",title.Trim());cmd.Parameters.AddWithValue("$d",DateTime.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$id",templateId);var created=Convert.ToInt64(cmd.ExecuteScalar());if(created==0)throw new InvalidOperationException("Шаблон не найден.");tx.Commit();RefreshSearchIndex(created);return created;}
    public void Move(long id, long? parentId)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Nodes SET ParentId=$p,ModifiedAt=$m WHERE Id=$id";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$p", parentId is null ? DBNull.Value : parentId); command.Parameters.AddWithValue("$m", DateTime.UtcNow.ToString("O")); command.ExecuteNonQuery();
    }
    public void Delete(long id) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "WITH RECURSIVE branch(Id) AS (SELECT $id UNION ALL SELECT n.Id FROM Nodes n JOIN branch b ON n.ParentId=b.Id) UPDATE Nodes SET DeletedAt=$d,ModifiedAt=$d WHERE Id IN branch"; cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$d", DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery(); }
    public void Restore(long id) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "WITH RECURSIVE branch(Id) AS (SELECT $id UNION ALL SELECT n.Id FROM Nodes n JOIN branch b ON n.ParentId=b.Id) UPDATE Nodes SET DeletedAt=NULL,ModifiedAt=$d WHERE Id IN branch"; cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$d", DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery(); }
    public void DeletePermanently(long id) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM SearchIndex WHERE CAST(NodeId AS INTEGER) IN (WITH RECURSIVE branch(Id) AS (SELECT $id UNION ALL SELECT n.Id FROM Nodes n JOIN branch b ON n.ParentId=b.Id) SELECT Id FROM branch); DELETE FROM Nodes WHERE Id=$id AND DeletedAt IS NOT NULL"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); }
    public void ToggleFavorite(long id) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE Nodes SET IsFavorite=CASE IsFavorite WHEN 0 THEN 1 ELSE 0 END WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); }
    public long Duplicate(long id) { using var c=Open(); using var tx=c.BeginTransaction(); using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="INSERT INTO Nodes(ParentId,IsFolder,Title,SortOrder,CreatedAt,ModifiedAt,Content,PlainText,Status) SELECT ParentId,IsFolder,Title || ' — копия',(SELECT COALESCE(MAX(SortOrder)+1,0) FROM Nodes x WHERE x.ParentId IS Nodes.ParentId),$d,$d,Content,PlainText,Status FROM Nodes WHERE Id=$id; SELECT last_insert_rowid();"; cmd.Parameters.AddWithValue("$id",id); cmd.Parameters.AddWithValue("$d",DateTime.UtcNow.ToString("O")); var copy=Convert.ToInt64(cmd.ExecuteScalar()); tx.Commit(); RefreshSearchIndex(copy); return copy; }

    public (FlowDocument Document, DateTime Created, DateTime Modified) LoadDocument(long id)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT Content,CreatedAt,ModifiedAt FROM Nodes WHERE Id=$id"; command.Parameters.AddWithValue("$id", id);
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
        using var index = connection.CreateCommand(); index.Transaction = transaction; index.CommandText = "DELETE FROM SearchIndex WHERE CAST(NodeId AS INTEGER)=$id; INSERT INTO SearchIndex(NodeId,Title,PlainText) SELECT Id,Title,PlainText FROM Nodes WHERE Id=$id; DELETE FROM DocumentVersions WHERE DocumentId=$id AND Id NOT IN(SELECT Id FROM DocumentVersions WHERE DocumentId=$id ORDER BY CreatedAt DESC LIMIT 100)"; index.Parameters.AddWithValue("$id", id); index.ExecuteNonQuery(); transaction.Commit();
    }
    public (string Json,string Html,string PlainText) LoadStructuredContent(long id){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT EditorJson,Html,PlainText FROM Nodes WHERE Id=$id";cmd.Parameters.AddWithValue("$id",id);using var r=cmd.ExecuteReader();if(!r.Read())throw new InvalidOperationException("Документ не найден.");return(r.GetString(0),r.GetString(1),r.GetString(2));}
    public void SaveStructuredContent(long id,string json,string html,string plainText){using var c=Open();using var tx=c.BeginTransaction();using(var version=c.CreateCommand()){version.Transaction=tx;version.CommandText="INSERT INTO DocumentVersions(DocumentId,CreatedAt,Reason,Content,PlainText,Size,EditorJson,Html) SELECT Id,$d,'Автосохранение',COALESCE(Content,X''),PlainText,length(COALESCE(Content,X'')),EditorJson,Html FROM Nodes WHERE Id=$id AND EditorJson<>'' AND EditorJson<>$j";version.Parameters.AddWithValue("$id",id);version.Parameters.AddWithValue("$d",DateTime.UtcNow.ToString("O"));version.Parameters.AddWithValue("$j",json);version.ExecuteNonQuery();}using(var cmd=c.CreateCommand()){cmd.Transaction=tx;cmd.CommandText="UPDATE Nodes SET EditorJson=$j,Html=$h,PlainText=$p,ModifiedAt=$d WHERE Id=$id;DELETE FROM SearchIndex WHERE CAST(NodeId AS INTEGER)=$id;INSERT INTO SearchIndex(NodeId,Title,PlainText) SELECT Id,Title,PlainText FROM Nodes WHERE Id=$id;DELETE FROM DocumentVersions WHERE DocumentId=$id AND Id NOT IN(SELECT Id FROM DocumentVersions WHERE DocumentId=$id ORDER BY CreatedAt DESC LIMIT 100)";cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$j",json);cmd.Parameters.AddWithValue("$h",html);cmd.Parameters.AddWithValue("$p",plainText);cmd.Parameters.AddWithValue("$d",DateTime.UtcNow.ToString("O"));cmd.ExecuteNonQuery();}tx.Commit();}

    public string? GetSetting(string key) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Value FROM Settings WHERE Key=$k"; cmd.Parameters.AddWithValue("$k", key); return cmd.ExecuteScalar() as string; }
    public void SetSetting(string key, string value) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT INTO Settings(Key,Value) VALUES($k,$v) ON CONFLICT(Key) DO UPDATE SET Value=$v"; cmd.Parameters.AddWithValue("$k", key); cmd.Parameters.AddWithValue("$v", value); cmd.ExecuteNonQuery(); }
    public void Checkpoint() { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; cmd.ExecuteNonQuery(); }
    public long CountDocuments() => Count("IsFolder=0 AND DeletedAt IS NULL");
    public long CountFavorites() => Count("IsFolder=0 AND IsFavorite=1 AND DeletedAt IS NULL");
    public long CountTrash() => Count("DeletedAt IS NOT NULL");
    public long CountRecent() => Count("IsFolder=0 AND LastOpenedAt IS NOT NULL AND DeletedAt IS NULL");
    public string CheckIntegrity() { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA integrity_check"; return Convert.ToString(cmd.ExecuteScalar()) ?? "unknown"; }
    public IReadOnlyList<DocumentVersion> GetVersions(long documentId) { using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Id,DocumentId,CreatedAt,Reason,Size FROM DocumentVersions WHERE DocumentId=$id ORDER BY CreatedAt DESC";cmd.Parameters.AddWithValue("$id",documentId);var list=new List<DocumentVersion>();using var r=cmd.ExecuteReader();while(r.Read())list.Add(new(r.GetInt64(0),r.GetInt64(1),DateTime.Parse(r.GetString(2)),r.GetString(3),r.GetInt64(4)));return list; }
    public void RestoreVersion(long versionId) { using var c=Open();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="UPDATE Nodes SET Content=(SELECT Content FROM DocumentVersions WHERE Id=$v),PlainText=(SELECT PlainText FROM DocumentVersions WHERE Id=$v),ModifiedAt=$d WHERE Id=(SELECT DocumentId FROM DocumentVersions WHERE Id=$v)";cmd.Parameters.AddWithValue("$v",versionId);cmd.Parameters.AddWithValue("$d",DateTime.UtcNow.ToString("O"));if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Версия не найдена.");tx.Commit(); }
    public long RegisterAttachment(long documentId,string fileName,string storedName,string mime,long size,string sha256){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO Attachments(DocumentId,FileName,StoredName,MimeType,Size,Sha256,CreatedAt) VALUES($d,$f,$s,$m,$z,$h,$c);SELECT last_insert_rowid();";cmd.Parameters.AddWithValue("$d",documentId);cmd.Parameters.AddWithValue("$f",fileName);cmd.Parameters.AddWithValue("$s",storedName);cmd.Parameters.AddWithValue("$m",mime);cmd.Parameters.AddWithValue("$z",size);cmd.Parameters.AddWithValue("$h",sha256);cmd.Parameters.AddWithValue("$c",DateTime.UtcNow.ToString("O"));return Convert.ToInt64(cmd.ExecuteScalar());}
    public IReadOnlyList<AttachmentInfo> GetAttachments(long documentId){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Id,DocumentId,FileName,StoredName,MimeType,Size,Sha256,CreatedAt FROM Attachments WHERE DocumentId=$id ORDER BY CreatedAt DESC";cmd.Parameters.AddWithValue("$id",documentId);var list=new List<AttachmentInfo>();using var r=cmd.ExecuteReader();while(r.Read())list.Add(new(r.GetInt64(0),r.GetInt64(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetInt64(5),r.GetString(6),DateTime.Parse(r.GetString(7))));return list;}
    public void SetStatus(long id,string status){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Nodes SET Status=$s,ModifiedAt=$d WHERE Id=$id";cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$s",status);cmd.Parameters.AddWithValue("$d",DateTime.UtcNow.ToString("O"));cmd.ExecuteNonQuery();}
    public string GetStatus(long id){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Status FROM Nodes WHERE Id=$id";cmd.Parameters.AddWithValue("$id",id);return Convert.ToString(cmd.ExecuteScalar())??"Черновик";}
    public void SetTags(long id,IEnumerable<string> tags){using var c=Open();using var tx=c.BeginTransaction();using(var clear=c.CreateCommand()){clear.Transaction=tx;clear.CommandText="DELETE FROM DocumentTags WHERE DocumentId=$id";clear.Parameters.AddWithValue("$id",id);clear.ExecuteNonQuery();}foreach(var value in tags.Select(x=>x.Trim()).Where(x=>x.Length>0).Distinct(StringComparer.OrdinalIgnoreCase)){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO Tags(Name) VALUES($n) ON CONFLICT(Name) DO NOTHING;INSERT INTO DocumentTags(DocumentId,TagId) SELECT $id,Id FROM Tags WHERE Name=$n";cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$n",value);cmd.ExecuteNonQuery();}tx.Commit();}
    public IReadOnlyList<string> GetTags(long id){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT t.Name FROM Tags t JOIN DocumentTags d ON d.TagId=t.Id WHERE d.DocumentId=$id ORDER BY t.Name";cmd.Parameters.AddWithValue("$id",id);var list=new List<string>();using var r=cmd.ExecuteReader();while(r.Read())list.Add(r.GetString(0));return list;}

    private void Execute(string sql, long id, string value) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$v", value); cmd.Parameters.AddWithValue("$m", DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery(); }
    private long Count(string where) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM Nodes WHERE " + where; return Convert.ToInt64(cmd.ExecuteScalar()); }
    private List<NodeItem> LoadFlat(string where, string order) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = $"SELECT Id,ParentId,IsFolder,Title,SortOrder,CreatedAt,ModifiedAt FROM Nodes WHERE {where} ORDER BY {order}"; var result = new List<NodeItem>(); using var reader = cmd.ExecuteReader(); while (reader.Read()) result.Add(new NodeItem { Id=reader.GetInt64(0), ParentId=reader.IsDBNull(1)?null:reader.GetInt64(1), IsFolder=reader.GetBoolean(2), Title=reader.GetString(3), SortOrder=reader.GetInt32(4), CreatedAt=DateTime.Parse(reader.GetString(5)), ModifiedAt=DateTime.Parse(reader.GetString(6)) }); return result; }
    private void RefreshSearchIndex(long id) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM SearchIndex WHERE CAST(NodeId AS INTEGER)=$id; INSERT INTO SearchIndex(NodeId,Title,PlainText) SELECT Id,Title,PlainText FROM Nodes WHERE Id=$id AND IsFolder=0"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); }
    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition) { using var check = connection.CreateCommand(); check.CommandText = $"PRAGMA table_info({table})"; using var reader = check.ExecuteReader(); while (reader.Read()) if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return; reader.Close(); using var alter = connection.CreateCommand(); alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}"; alter.ExecuteNonQuery(); }
    private static byte[] Serialize(FlowDocument document) { using var stream = new MemoryStream(); var range = new TextRange(document.ContentStart, document.ContentEnd); range.Save(stream, DataFormats.XamlPackage); return stream.ToArray(); }
    private static FlowDocument Deserialize(byte[] data) { var doc = new FlowDocument(); using var stream = new MemoryStream(data); new TextRange(doc.ContentStart, doc.ContentEnd).Load(stream, DataFormats.XamlPackage); return doc; }

    private void CreateDemo(SqliteConnection connection)
    {
        var folder = Create(null, true, "Начало работы");
        var id = Create(folder, false, "Возможности MasterDocumentation");
        var doc = new FlowDocument(); doc.Blocks.Add(new Paragraph(new Run("MasterDocumentation")) { FontSize = 26, FontWeight = FontWeights.Bold });
        doc.Blocks.Add(new Paragraph(new Run("Это переносимая база документации с форматированием, поиском и резервными копиями.")));
        doc.Blocks.Add(new List(new ListItem(new Paragraph(new Run("Создавайте папки и документы")))) { ListItems = { new ListItem(new Paragraph(new Run("Форматируйте текст и вставляйте таблицы"))), new ListItem(new Paragraph(new Run("Данные сохраняются автоматически"))) } });
        var table = new Table(); table.Columns.Add(new TableColumn()); table.Columns.Add(new TableColumn()); var group = new TableRowGroup(); table.RowGroups.Add(group);
        var row1 = new TableRow(); row1.Cells.Add(new TableCell(new Paragraph(new Run("Возможность")))); row1.Cells.Add(new TableCell(new Paragraph(new Run("Описание")))); group.Rows.Add(row1);
        var row2 = new TableRow(); row2.Cells.Add(new TableCell(new Paragraph(new Run("Хранение")))); row2.Cells.Add(new TableCell(new Paragraph(new Run("SQLite + XamlPackage")))); group.Rows.Add(row2); doc.Blocks.Add(table);
        SaveDocument(id, doc, new TextRange(doc.ContentStart, doc.ContentEnd).Text);
    }
}

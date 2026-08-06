using MasterDocumentation.Models;
using MasterDocumentation.Services;
using System.Windows.Documents;

namespace MasterDocumentation.Storage;

/// <summary>
/// Фасад хранилища документации. Публичное имя и API класса не меняются — весь остальной
/// код (окна, вьюмодели, DI, тесты) продолжает работать с <see cref="DatabaseService"/> как раньше.
/// Внутри класс лишь выбирает конкретную реализацию <see cref="IDocumentStore"/> согласно
/// <see cref="StorageConfigService"/> (SQLite по умолчанию, либо PostgreSQL, если так настроено)
/// и делегирует ей все вызовы. Новый провайдер БД подключается реализацией IDocumentStore
/// и веткой в <see cref="CreateStore"/> — без изменений в вызывающем коде.
/// </summary>
public sealed class DatabaseService : IDocumentStore
{
    private readonly IDocumentStore _store;

    public DatabaseService() : this(StorageConfigService.Load()) { }

    public DatabaseService(StorageProviderConfig config)
    {
        _store = CreateStore(config);
    }

    /// <summary>Для сценариев миграции/тестирования — явное указание готового провайдера.</summary>
    public DatabaseService(IDocumentStore store)
    {
        _store = store;
    }

    /// <summary>Провайдер, который фактически используется (может отличаться от настроек при сбое подключения).</summary>
    public StorageProviderKind ActiveProvider => _store is PostgresDocumentStore ? StorageProviderKind.Postgres : StorageProviderKind.Sqlite;

    private static IDocumentStore CreateStore(StorageProviderConfig config)
    {
        if (config.Provider == StorageProviderKind.Postgres)
        {
            if (!string.IsNullOrWhiteSpace(config.PostgresConnectionString))
            {
                try { return new PostgresDocumentStore(config.PostgresConnectionString); }
                catch (Exception ex) { LogService.Error("Не удалось подключиться к PostgreSQL по сохранённым настройкам, используется локальное хранилище SQLite.", ex); }
            }
            else
            {
                LogService.Error("Выбран провайдер PostgreSQL, но строка подключения не задана — используется локальное хранилище SQLite.", null);
            }
        }
        return new SqliteDocumentStore();
    }

    public void Initialize() => _store.Initialize();

    public List<NodeItem> LoadTree(string? query = null) => _store.LoadTree(query);
    public List<NodeItem> LoadFavorites() => _store.LoadFavorites();
    public List<NodeItem> LoadRecent(int limit = 25) => _store.LoadRecent(limit);
    public List<NodeItem> LoadTrash() => _store.LoadTrash();
    public List<NodeItem> LoadTemplates() => _store.LoadTemplates();
    public NodeItem? FindNode(long id) => _store.FindNode(id);
    public long Create(long? parentId, bool folder, string title) => _store.Create(parentId, folder, title);
    public long Create(long? parentId, bool folder, string title, bool isPrivate) => _store.Create(parentId, folder, title, isPrivate);
    public bool TitleExists(long? parentId, string title, long? exceptId = null) => _store.TitleExists(parentId, title, exceptId);
    public void Rename(long id, string title) { _store.Rename(id, title); MirrorMarkdown(id); }
    public void SetTemplate(long id, bool value) => _store.SetTemplate(id, value);
    public long CreateFromTemplate(long templateId, long? parentId, string title, IReadOnlyDictionary<string, string>? variables = null) => _store.CreateFromTemplate(templateId, parentId, title, variables);
    public IReadOnlyList<string> GetTemplateVariables(long templateId) => _store.GetTemplateVariables(templateId);
    public string GetDocumentGuid(long id) => _store.GetDocumentGuid(id);
    public NodeItem? FindDocumentByGuid(string guid) => _store.FindDocumentByGuid(guid);
    public void Move(long id, long? parentId) => _store.Move(id, parentId);
    public void Delete(long id) => _store.Delete(id);
    public void Restore(long id) => _store.Restore(id);
    public void DeletePermanently(long id)
    {
        string guid;
        try { guid = _store.GetDocumentGuid(id); } catch { guid = ""; }
        _store.DeletePermanently(id);
        if (guid.Length > 0) MarkdownMirrorService.Delete(guid);
    }
    public void ToggleFavorite(long id) => _store.ToggleFavorite(id);
    public long Duplicate(long id) => _store.Duplicate(id);
    public void EmptyTrash() => _store.EmptyTrash();
    /// <summary>Срок хранения в корзине: после него элементы удаляются окончательно без подтверждения.</summary>
    public const int TrashRetentionDays = 30;
    public int PurgeExpiredTrash(int retentionDays = TrashRetentionDays) => _store.PurgeExpiredTrash(retentionDays);

    public (FlowDocument Document, DateTime Created, DateTime Modified) LoadDocument(long id) => _store.LoadDocument(id);
    public void SaveDocument(long id, FlowDocument document, string plainText) { _store.SaveDocument(id, document, plainText); MirrorMarkdown(id); }
    public (string Json, string Html, string PlainText) LoadStructuredContent(long id)
    {
        var content = _store.LoadStructuredContent(id);
        AssetSyncService.MaterializeDocumentAssets(_store, id);
        return content;
    }

    /// <summary>
    /// Сохраняет страницу: содержимое редактора, её Markdown-представление (.md-зеркало на диске)
    /// и двоичное содержимое всех изображений — в базу данных, чтобы картинки открывались
    /// у любого пользователя этой базы, а не только на компьютере автора.
    /// </summary>
    public void SaveStructuredContent(long id, string json, string html, string plainText)
    {
        _store.SaveStructuredContent(id, json, html, plainText);
        AssetSyncService.PublishDocumentAssets(_store, id);
        MirrorMarkdown(id);
    }

    public string GetDocumentPlainText(long documentId) => _store.GetDocumentPlainText(documentId);
    public double GetZoom(long id) => _store.GetZoom(id);
    public void SetZoom(long id, double value) => _store.SetZoom(id, value);

    public string GetDocumentMarkdown(long documentId) => _store.GetDocumentMarkdown(documentId);
    public void SetDocumentMarkdown(long documentId, string markdown) { _store.SetDocumentMarkdown(documentId, markdown); MirrorMarkdown(documentId); }
    public DocumentAccess GetDocumentAccess(long documentId) => _store.GetDocumentAccess(documentId);
    public void SetDocumentAccess(long documentId, bool isPrivate) => _store.SetDocumentAccess(documentId, isPrivate);
    public void SaveAssetContent(string storedName, string sha256, string mimeType, byte[] data) => _store.SaveAssetContent(storedName, sha256, mimeType, data);
    public (byte[] Data, string MimeType)? LoadAssetContent(string storedName) => _store.LoadAssetContent(storedName);
    public bool AssetExists(string storedName) => _store.AssetExists(storedName);
    public IReadOnlyList<string> GetReferencedAssetNames(long documentId) => _store.GetReferencedAssetNames(documentId);

    /// <summary>Записывает файл вложения и в локальную папку Assets, и в базу данных.</summary>
    public void StoreAssetContent(string storedName, string sha256, string mimeType, byte[] data) => AssetSyncService.Store(_store, storedName, sha256, mimeType, data);
    /// <summary>Восстанавливает локальную копию файла вложения из базы данных, если её нет на диске.</summary>
    public bool MaterializeAsset(string storedName) => AssetSyncService.Materialize(_store, storedName);

    private void MirrorMarkdown(long id)
    {
        try
        {
            var node = _store.FindNode(id);
            if (node is null || node.IsFolder) return;
            MarkdownMirrorService.Write(_store.GetDocumentGuid(id), node.Title, _store.GetDocumentMarkdown(id));
        }
        catch (Exception ex) { LogService.Error("Не удалось обновить .md-зеркало документа " + id, ex); }
    }

    public string? GetSetting(string key) => _store.GetSetting(key);
    public void SetSetting(string key, string value) => _store.SetSetting(key, value);
    public void Checkpoint() => _store.Checkpoint();
    public string CheckIntegrity() => _store.CheckIntegrity();
    public long CountDocuments() => _store.CountDocuments();
    public long CountFavorites() => _store.CountFavorites();
    public long CountTrash() => _store.CountTrash();
    public long CountRecent() => _store.CountRecent();

    public IReadOnlyList<DocumentVersion> GetVersions(long documentId) => _store.GetVersions(documentId);
    public string GetVersionPlainText(long versionId) => _store.GetVersionPlainText(versionId);
    public void ToggleVersionPin(long versionId) => _store.ToggleVersionPin(versionId);
    public void DeleteVersion(long versionId) => _store.DeleteVersion(versionId);
    public void RestoreVersion(long versionId) => _store.RestoreVersion(versionId);

    public long RegisterAttachment(long documentId, string fileName, string storedName, string mime, long size, string sha256)
    {
        var result = _store.RegisterAttachment(documentId, fileName, storedName, mime, size, sha256);
        AssetSyncService.Publish(_store, storedName);
        return result;
    }
    public string? FindStoredAttachmentByHash(string sha256) => _store.FindStoredAttachmentByHash(sha256);
    public IReadOnlyList<AttachmentInfo> GetAttachments(long documentId) => _store.GetAttachments(documentId);
    public (string StoredName, bool IsUnused) RemoveAttachment(long attachmentId, bool ignoreVersions = false) => _store.RemoveAttachment(attachmentId, ignoreVersions);
    public void CleanupUnusedAssets() => _store.CleanupUnusedAssets();

    public void SetStatus(long id, string status) => _store.SetStatus(id, status);
    public string GetStatus(long id) => _store.GetStatus(id);
    public IReadOnlyList<string> GetStatuses() => _store.GetStatuses();
    public void AddStatus(string name) => _store.AddStatus(name);
    public void DeleteStatus(string name) => _store.DeleteStatus(name);

    public DocumentMetadata GetDocumentMetadata(long id) => _store.GetDocumentMetadata(id);
    public IReadOnlyList<CustomPropertyValue> GetCustomProperties(long documentId) => _store.GetCustomProperties(documentId);
    public void SetCustomProperties(long documentId, IEnumerable<CustomPropertyValue> properties) => _store.SetCustomProperties(documentId, properties);
    public void SetTags(long id, IEnumerable<string> tags) => _store.SetTags(id, tags);
    public IReadOnlyList<string> GetTags(long id) => _store.GetTags(id);

    public IReadOnlyList<DocumentSearchResult> SearchDocuments(string? query, string? status = null, string? tag = null, bool favoritesOnly = false, bool attachmentsOnly = false, bool includeTrash = false, bool sortByDate = false, int limit = 200) => _store.SearchDocuments(query, status, tag, favoritesOnly, attachmentsOnly, includeTrash, sortByDate, limit);
    public void RebuildSearchIndex() => _store.RebuildSearchIndex();
}

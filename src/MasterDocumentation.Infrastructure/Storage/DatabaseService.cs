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
    public bool TitleExists(long? parentId, string title, long? exceptId = null) => _store.TitleExists(parentId, title, exceptId);
    public void Rename(long id, string title) => _store.Rename(id, title);
    public void SetTemplate(long id, bool value) => _store.SetTemplate(id, value);
    public long CreateFromTemplate(long templateId, long? parentId, string title, IReadOnlyDictionary<string, string>? variables = null) => _store.CreateFromTemplate(templateId, parentId, title, variables);
    public IReadOnlyList<string> GetTemplateVariables(long templateId) => _store.GetTemplateVariables(templateId);
    public string GetDocumentGuid(long id) => _store.GetDocumentGuid(id);
    public NodeItem? FindDocumentByGuid(string guid) => _store.FindDocumentByGuid(guid);
    public void Move(long id, long? parentId) => _store.Move(id, parentId);
    public void Delete(long id) => _store.Delete(id);
    public void Restore(long id) => _store.Restore(id);
    public void DeletePermanently(long id) => _store.DeletePermanently(id);
    public void ToggleFavorite(long id) => _store.ToggleFavorite(id);
    public long Duplicate(long id) => _store.Duplicate(id);
    public void EmptyTrash() => _store.EmptyTrash();

    public (FlowDocument Document, DateTime Created, DateTime Modified) LoadDocument(long id) => _store.LoadDocument(id);
    public void SaveDocument(long id, FlowDocument document, string plainText) => _store.SaveDocument(id, document, plainText);
    public (string Json, string Html, string PlainText) LoadStructuredContent(long id) => _store.LoadStructuredContent(id);
    public void SaveStructuredContent(long id, string json, string html, string plainText) => _store.SaveStructuredContent(id, json, html, plainText);
    public string GetDocumentPlainText(long documentId) => _store.GetDocumentPlainText(documentId);
    public double GetZoom(long id) => _store.GetZoom(id);
    public void SetZoom(long id, double value) => _store.SetZoom(id, value);

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

    public long RegisterAttachment(long documentId, string fileName, string storedName, string mime, long size, string sha256) => _store.RegisterAttachment(documentId, fileName, storedName, mime, size, sha256);
    public string? FindStoredAttachmentByHash(string sha256) => _store.FindStoredAttachmentByHash(sha256);
    public IReadOnlyList<AttachmentInfo> GetAttachments(long documentId) => _store.GetAttachments(documentId);
    public (string StoredName, bool IsUnused) RemoveAttachment(long attachmentId) => _store.RemoveAttachment(attachmentId);
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

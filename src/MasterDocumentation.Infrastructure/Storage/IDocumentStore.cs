using System.Windows.Documents;
using MasterDocumentation.Models;

namespace MasterDocumentation.Storage;

/// <summary>
/// Контракт хранилища документации. Любой провайдер (SQLite, Postgres, в будущем — другие СУБД)
/// реализует этот интерфейс и подключается без изменений в вызывающем коде — весь UI и остальные
/// проекты работают через <see cref="DatabaseService"/>, которая лишь выбирает и делегирует вызовы
/// конкретной реализации в зависимости от настроек хранилища (см. StorageSettings/StorageConfigService).
/// </summary>
public interface IDocumentStore
{
    void Initialize();

    // Дерево документов и папок
    List<NodeItem> LoadTree(string? query = null);
    List<NodeItem> LoadFavorites();
    List<NodeItem> LoadRecent(int limit = 25);
    List<NodeItem> LoadTrash();
    List<NodeItem> LoadTemplates();
    NodeItem? FindNode(long id);
    long Create(long? parentId, bool folder, string title);
    long Create(long? parentId, bool folder, string title, bool isPrivate);
    bool TitleExists(long? parentId, string title, long? exceptId = null);
    void Rename(long id, string title);
    void SetTemplate(long id, bool value);
    long CreateFromTemplate(long templateId, long? parentId, string title, IReadOnlyDictionary<string, string>? variables = null);
    IReadOnlyList<string> GetTemplateVariables(long templateId);
    string GetDocumentGuid(long id);
    NodeItem? FindDocumentByGuid(string guid);
    void Move(long id, long? parentId);
    void Delete(long id);
    void Restore(long id);
    void DeletePermanently(long id);
    void ToggleFavorite(long id);
    long Duplicate(long id);
    void EmptyTrash();
    /// <summary>Окончательно удаляет элементы, пролежавшие в корзине дольше указанного срока. Возвращает их количество.</summary>
    int PurgeExpiredTrash(int retentionDays);

    // Содержимое документа
    (FlowDocument Document, DateTime Created, DateTime Modified) LoadDocument(long id);
    void SaveDocument(long id, FlowDocument document, string plainText);
    (string Json, string Html, string PlainText) LoadStructuredContent(long id);
    void SaveStructuredContent(long id, string json, string html, string plainText);
    string GetDocumentPlainText(long documentId);
    double GetZoom(long id);
    void SetZoom(long id, double value);

    // Markdown — канонический формат страницы документации (.md)
    string GetDocumentMarkdown(long documentId);
    void SetDocumentMarkdown(long documentId, string markdown);

    // Доступ к странице: приватная видна только владельцу, обычная — всем подключившимся к БД
    DocumentAccess GetDocumentAccess(long documentId);
    void SetDocumentAccess(long documentId, bool isPrivate);

    // Двоичное содержимое вложений и изображений (хранится в БД по SHA-256, чтобы картинки
    // отображались у всех, кто подключён к этой базе, а не только на компьютере автора)
    void SaveAssetContent(string storedName, string sha256, string mimeType, byte[] data);
    (byte[] Data, string MimeType)? LoadAssetContent(string storedName);
    bool AssetExists(string storedName);
    IReadOnlyList<string> GetReferencedAssetNames(long documentId);

    // Настройки и обслуживание хранилища
    string? GetSetting(string key);
    void SetSetting(string key, string value);
    void Checkpoint();
    string CheckIntegrity();
    long CountDocuments();
    long CountFavorites();
    long CountTrash();
    long CountRecent();

    // Версии документа
    IReadOnlyList<DocumentVersion> GetVersions(long documentId);
    string GetVersionPlainText(long versionId);
    void ToggleVersionPin(long versionId);
    void DeleteVersion(long versionId);
    void RestoreVersion(long versionId);

    // Вложения
    long RegisterAttachment(long documentId, string fileName, string storedName, string mime, long size, string sha256);
    string? FindStoredAttachmentByHash(string sha256);
    IReadOnlyList<AttachmentInfo> GetAttachments(long documentId);
    (string StoredName, bool IsUnused) RemoveAttachment(long attachmentId);
    void CleanupUnusedAssets();

    // Статусы
    void SetStatus(long id, string status);
    string GetStatus(long id);
    IReadOnlyList<string> GetStatuses();
    void AddStatus(string name);
    void DeleteStatus(string name);

    // Свойства, метаданные, теги
    DocumentMetadata GetDocumentMetadata(long id);
    IReadOnlyList<CustomPropertyValue> GetCustomProperties(long documentId);
    void SetCustomProperties(long documentId, IEnumerable<CustomPropertyValue> properties);
    void SetTags(long id, IEnumerable<string> tags);
    IReadOnlyList<string> GetTags(long id);

    // Поиск
    IReadOnlyList<DocumentSearchResult> SearchDocuments(string? query, string? status = null, string? tag = null, bool favoritesOnly = false, bool attachmentsOnly = false, bool includeTrash = false, bool sortByDate = false, int limit = 200);
    void RebuildSearchIndex();
}

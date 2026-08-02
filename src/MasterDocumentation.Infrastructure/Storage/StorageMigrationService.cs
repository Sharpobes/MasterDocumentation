using System.IO;
using System.Security.Cryptography;
using MasterDocumentation.Models;

namespace MasterDocumentation.Storage;

/// <summary>
/// Перенос документации между двумя реализациями <see cref="IDocumentStore"/> (например,
/// из локального SQLite в PostgreSQL и обратно — импорт из общей базы к себе). Работает
/// исключительно через интерфейс, поэтому годится для переноса в любую сторону и между
/// любыми будущими провайдерами.
///
/// Переносится дерево, содержимое (включая Markdown-представление страницы), статусы, теги,
/// свойства, вложения вместе с двоичным содержимым изображений и признак приватности страницы.
/// Не переносятся: история версий документов и общий реестр Settings — IDocumentStore не даёт
/// перечислить их целиком.
/// </summary>
public static class StorageMigrationService
{
    /// <summary>Полный перенос всей документации.</summary>
    public static void CopyAll(IDocumentStore source, IDocumentStore target, IProgress<string>? progress = null)
        => Copy(source, target, null, null, progress);

    /// <summary>
    /// Перенос только выбранных документов: вместе с ними создаётся цепочка родительских папок,
    /// уже существующие папки переиспользуются. <paramref name="forcePrivate"/> позволяет пометить
    /// все переносимые страницы приватными (видны только текущему пользователю) или, наоборот,
    /// общими; null — сохранить признак исходного документа.
    /// </summary>
    public static void CopySelected(IDocumentStore source, IDocumentStore target, IEnumerable<long> documentIds, bool? forcePrivate = null, IProgress<string>? progress = null)
    {
        var selected = documentIds.ToHashSet();
        if (selected.Count == 0) throw new InvalidOperationException("Не выбрано ни одного документа.");
        Copy(source, target, selected, forcePrivate, progress);
    }

    private static void Copy(IDocumentStore source, IDocumentStore target, HashSet<long>? selected, bool? forcePrivate, IProgress<string>? progress)
    {
        target.Initialize();

        progress?.Report("Перенос статусов документов…");
        foreach (var status in source.GetStatuses())
        {
            try { target.AddStatus(status); } catch (InvalidOperationException) { /* статус уже существует (встроенный) */ }
        }

        var tree = source.LoadTree();
        var favorites = source.LoadFavorites().Select(x => x.Id).ToHashSet();
        var templates = source.LoadTemplates().Select(x => x.Id).ToHashSet();
        var required = selected is null ? null : RequiredNodes(tree, selected);
        var idMap = new Dictionary<long, long>();
        var existingTitles = BuildTitleIndex(target);

        progress?.Report("Перенос дерева документов и папок…");
        var copied = 0;
        void CopyNode(NodeItem node, long? newParentId)
        {
            if (required is not null && !required.Contains(node.Id)) return;
            long newId;
            if (node.IsFolder)
            {
                newId = EnsureFolder(target, existingTitles, newParentId, node.Title);
            }
            else
            {
                var isPrivate = forcePrivate ?? source.GetDocumentAccess(node.Id).IsPrivate;
                newId = target.Create(newParentId, false, UniqueTitle(existingTitles, newParentId, node.Title), isPrivate);
                var (document, _, _) = source.LoadDocument(node.Id);
                var plainText = source.GetDocumentPlainText(node.Id);
                target.SaveDocument(newId, document, plainText);
                var (json, html, structuredPlainText) = source.LoadStructuredContent(node.Id);
                target.SaveStructuredContent(newId, json, html, structuredPlainText);
                target.SetDocumentMarkdown(newId, source.GetDocumentMarkdown(node.Id));
                target.SetDocumentAccess(newId, isPrivate);
                target.SetStatus(newId, source.GetStatus(node.Id));
                target.SetTags(newId, source.GetTags(node.Id));
                target.SetCustomProperties(newId, source.GetCustomProperties(node.Id));
                target.SetZoom(newId, source.GetZoom(node.Id));
                foreach (var attachment in source.GetAttachments(node.Id))
                    target.RegisterAttachment(newId, attachment.FileName, attachment.StoredName, attachment.MimeType, attachment.Size, attachment.Sha256);
                foreach (var asset in source.GetReferencedAssetNames(node.Id)) CopyAsset(source, target, asset);
                copied++;
                if (copied % 10 == 0) progress?.Report($"Перенесено документов: {copied}…");
            }
            idMap[node.Id] = newId;
            foreach (var child in node.Children) CopyNode(child, newId);
        }
        foreach (var root in tree) CopyNode(root, null);

        progress?.Report("Восстановление избранного и шаблонов…");
        foreach (var oldId in favorites) if (idMap.TryGetValue(oldId, out var newId)) target.ToggleFavorite(newId);
        foreach (var oldId in templates) if (idMap.TryGetValue(oldId, out var newId)) target.SetTemplate(newId, true);

        progress?.Report("Перестроение поискового индекса…");
        target.RebuildSearchIndex();
        progress?.Report($"Готово. Элементов перенесено: {idMap.Count} (документов: {copied}).");
    }

    /// <summary>Выбранные документы вместе со всеми их родительскими папками.</summary>
    private static HashSet<long> RequiredNodes(IEnumerable<NodeItem> tree, HashSet<long> selected)
    {
        var result = new HashSet<long>();
        bool Walk(NodeItem node, List<long> path)
        {
            path.Add(node.Id);
            var keep = selected.Contains(node.Id) && !node.IsFolder;
            foreach (var child in node.Children) keep |= Walk(child, path);
            if (keep) foreach (var id in path) result.Add(id);
            path.RemoveAt(path.Count - 1);
            return keep;
        }
        foreach (var root in tree) Walk(root, []);
        return result;
    }

    private static Dictionary<(long? Parent, string Title), long> BuildTitleIndex(IDocumentStore target)
    {
        var index = new Dictionary<(long?, string), long>(TitleComparer.Instance);
        void Walk(NodeItem node)
        {
            index[(node.ParentId, node.Title)] = node.Id;
            foreach (var child in node.Children) Walk(child);
        }
        foreach (var root in target.LoadTree()) Walk(root);
        return index;
    }

    private static long EnsureFolder(IDocumentStore target, Dictionary<(long? Parent, string Title), long> index, long? parentId, string title)
    {
        if (index.TryGetValue((parentId, title), out var existing)) return existing;
        var id = target.Create(parentId, true, title);
        index[(parentId, title)] = id;
        return id;
    }

    private static string UniqueTitle(Dictionary<(long? Parent, string Title), long> index, long? parentId, string title)
    {
        if (!index.ContainsKey((parentId, title))) { index[(parentId, title)] = 0; return title; }
        for (var i = 2; ; i++)
        {
            var candidate = $"{title} (импорт {i})";
            if (index.ContainsKey((parentId, candidate))) continue;
            index[(parentId, candidate)] = 0;
            return candidate;
        }
    }

    /// <summary>Копирует двоичное содержимое изображения или вложения, чтобы оно открывалось в целевой базе.</summary>
    private static void CopyAsset(IDocumentStore source, IDocumentStore target, string storedName)
    {
        try
        {
            if (target.AssetExists(storedName)) return;
            var content = source.LoadAssetContent(storedName);
            if (content is null)
            {
                var path = AssetSyncService.LocalPath(storedName);
                if (path is null || !File.Exists(path)) return;
                content = (File.ReadAllBytes(path), AssetSyncService.GuessMimeType(storedName));
            }
            target.SaveAssetContent(storedName, Convert.ToHexString(SHA256.HashData(content.Value.Data)), content.Value.MimeType, content.Value.Data);
        }
        catch (Exception ex) { Services.LogService.Error("Не удалось перенести файл вложения " + storedName, ex); }
    }

    private sealed class TitleComparer : IEqualityComparer<(long? Parent, string Title)>
    {
        public static readonly TitleComparer Instance = new();
        public bool Equals((long? Parent, string Title) x, (long? Parent, string Title) y) => x.Parent == y.Parent && string.Equals(x.Title, y.Title, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((long? Parent, string Title) obj) => HashCode.Combine(obj.Parent, obj.Title.ToLowerInvariant());
    }
}

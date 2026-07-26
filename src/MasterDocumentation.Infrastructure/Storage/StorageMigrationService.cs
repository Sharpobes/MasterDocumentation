namespace MasterDocumentation.Storage;

/// <summary>
/// Перенос документации между двумя реализациями <see cref="IDocumentStore"/> (например,
/// из локального SQLite в PostgreSQL). Работает исключительно через интерфейс — не знает,
/// с какими конкретно провайдерами имеет дело, поэтому годится для переноса в любую сторону
/// и между любыми будущими провайдерами.
///
/// Известные ограничения переноса: история версий документов (DocumentVersions) не копируется —
/// переносится только текущее содержимое; общий ключ-значение реестр Settings также не
/// переносится, так как IDocumentStore не даёт перечислить все ключи разом.
/// </summary>
public static class StorageMigrationService
{
    public static void CopyAll(IDocumentStore source, IDocumentStore target, IProgress<string>? progress = null)
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
        var idMap = new Dictionary<long, long>();

        progress?.Report("Перенос дерева документов и папок…");
        var copied = 0;
        void CopyNode(Models.NodeItem node, long? newParentId)
        {
            long newId;
            if (node.IsFolder)
            {
                newId = target.Create(newParentId, true, node.Title);
            }
            else
            {
                newId = target.Create(newParentId, false, node.Title);
                var (document, _, _) = source.LoadDocument(node.Id);
                var plainText = source.GetDocumentPlainText(node.Id);
                target.SaveDocument(newId, document, plainText);
                var (json, html, structuredPlainText) = source.LoadStructuredContent(node.Id);
                target.SaveStructuredContent(newId, json, html, structuredPlainText);
                target.SetStatus(newId, source.GetStatus(node.Id));
                target.SetTags(newId, source.GetTags(node.Id));
                target.SetCustomProperties(newId, source.GetCustomProperties(node.Id));
                target.SetZoom(newId, source.GetZoom(node.Id));
                foreach (var attachment in source.GetAttachments(node.Id))
                    target.RegisterAttachment(newId, attachment.FileName, attachment.StoredName, attachment.MimeType, attachment.Size, attachment.Sha256);
                copied++;
                if (copied % 25 == 0) progress?.Report($"Перенесено документов: {copied}…");
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
}

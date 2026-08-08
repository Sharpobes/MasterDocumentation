using System.IO;
using System.Security.Cryptography;
using MasterDocumentation.Models;
using MasterDocumentation.Services;

namespace MasterDocumentation.Storage;

/// <summary>Файл документации, который не удалось перенести, и причина отказа.</summary>
public sealed record AssetTransferFailure(string StoredName, string DocumentTitle, string Reason);

/// <summary>Страница, которую не удалось перенести целиком, и причина отказа.</summary>
public sealed record DocumentTransferFailure(string Title, string Reason);

/// <summary>
/// Итог переноса. Перенос не прерывается на первой ошибке: сбойные страницы и файлы
/// собираются здесь, чтобы пользователь увидел, что именно не доехало, и мог повторить
/// перенос только изображений (<see cref="StorageMigrationService.CopyAssets"/>), не гоняя
/// заново всю документацию.
/// </summary>
public sealed class MigrationReport
{
    public int Folders { get; internal set; }
    public int Documents { get; internal set; }
    /// <summary>Сколько из них обновлено на месте (страница уже была в целевой базе).</summary>
    public int Updated { get; internal set; }
    public int Assets { get; internal set; }
    public List<AssetTransferFailure> FailedAssets { get; } = [];
    public List<DocumentTransferFailure> FailedDocuments { get; } = [];
    public bool HasFailures => FailedAssets.Count > 0 || FailedDocuments.Count > 0;

    public IReadOnlyList<string> FailedAssetNames => FailedAssets.Select(x => x.StoredName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Короткая сводка для полосы состояния.</summary>
    public string Summary()
    {
        var text = $"Перенесено: страниц — {Documents} (из них обновлено: {Updated}), папок — {Folders}, файлов — {Assets}.";
        if (FailedDocuments.Count > 0) text += $" Не перенесено страниц: {FailedDocuments.Count}.";
        if (FailedAssets.Count > 0) text += $" Не перенесено изображений и вложений: {FailedAssetNames.Count}.";
        return text;
    }

    /// <summary>Подробный отчёт для диалога: что не перенеслось и по какой причине.</summary>
    public string Details()
    {
        var lines = new List<string> { Summary() };
        if (FailedDocuments.Count > 0)
        {
            lines.Add("");
            lines.Add("Страницы, которые не удалось перенести:");
            lines.AddRange(FailedDocuments.Take(20).Select(x => $"  • {x.Title} — {x.Reason}"));
            if (FailedDocuments.Count > 20) lines.Add($"  … и ещё {FailedDocuments.Count - 20}. Полный список — в журнале приложения.");
        }
        if (FailedAssets.Count > 0)
        {
            lines.Add("");
            lines.Add("Изображения и вложения, которые не удалось перенести:");
            lines.AddRange(FailedAssets.Take(20).Select(x => $"  • {x.StoredName}{(x.DocumentTitle.Length > 0 ? $" (страница «{x.DocumentTitle}»)" : "")} — {x.Reason}"));
            if (FailedAssets.Count > 20) lines.Add($"  … и ещё {FailedAssets.Count - 20}. Полный список — в журнале приложения.");
            lines.Add("");
            lines.Add("Сами страницы при этом перенесены — не хватает только файлов. Устраните причину (обычно это права пользователя базы на запись в таблицу assets) и нажмите «Повторить перенос изображений»: заново поедут только файлы, документация повторно копироваться не будет.");
        }
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Перенос документации между двумя реализациями <see cref="IDocumentStore"/> (например,
/// из локального SQLite в PostgreSQL и обратно — импорт из общей базы к себе). Работает
/// исключительно через интерфейс, поэтому годится для переноса в любую сторону и между
/// любыми будущими провайдерами.
///
/// Переносится дерево, содержимое (включая Markdown-представление страницы), статусы, теги,
/// свойства, вложения вместе с двоичным содержимым изображений и признак приватности страницы.
/// Не переносятся: история версий документов и состояние интерфейса рабочего места (оно вообще
/// не хранится в базе — см. <see cref="LocalStateService"/>).
///
/// Перенос идемпотентен: страница узнаётся по постоянному идентификатору (Guid), поэтому
/// повторная выгрузка тех же документов обновляет их в целевой базе, а не создаёт копии —
/// ни страниц, ни записей о вложениях. Новая страница получает идентификатор оригинала, так
/// что связь сохраняется и в обратную сторону (импорт из базы к себе и снова выгрузка).
///
/// Перенос рассчитан на одновременную работу нескольких пользователей с одной базой:
/// существующие папки переиспользуются, конфликт названий (кто-то создал такой же элемент
/// параллельно) не роняет перенос, а приводит к переиспользованию папки или к следующему
/// свободному названию документа; файлы адресуются по SHA-256, поэтому один и тот же рисунок,
/// выгруженный тремя людьми, остаётся в базе одной записью.
/// </summary>
public static class StorageMigrationService
{
    /// <summary>Полный перенос всей документации.</summary>
    public static MigrationReport CopyAll(IDocumentStore source, IDocumentStore target, IProgress<string>? progress = null)
        => Copy(source, target, null, null, progress);

    /// <summary>
    /// Перенос только выбранных документов: вместе с ними создаётся цепочка родительских папок,
    /// уже существующие папки переиспользуются. <paramref name="forcePrivate"/> позволяет пометить
    /// все переносимые страницы приватными (видны только текущему пользователю) или, наоборот,
    /// общими; null — сохранить признак исходного документа.
    /// </summary>
    public static MigrationReport CopySelected(IDocumentStore source, IDocumentStore target, IEnumerable<long> documentIds, bool? forcePrivate = null, IProgress<string>? progress = null)
    {
        var selected = documentIds.ToHashSet();
        if (selected.Count == 0) throw new InvalidOperationException("Не выбрано ни одного документа.");
        return Copy(source, target, selected, forcePrivate, progress);
    }

    /// <summary>
    /// Повторный перенос только файлов (изображений и вложений) — без документации. Нужен, когда
    /// страницы уехали, а файлы нет: обычно из-за прав пользователя базы. Сами страницы при этом
    /// не трогаются и не дублируются.
    /// </summary>
    public static MigrationReport CopyAssets(IDocumentStore source, IDocumentStore target, IEnumerable<string> storedNames, IProgress<string>? progress = null)
    {
        var report = new MigrationReport();
        var names = storedNames.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0) return report;
        progress?.Report($"Повторный перенос файлов: {names.Count}…");
        for (var i = 0; i < names.Count; i++)
        {
            CopyAsset(source, target, names[i], "", report);
            if ((i + 1) % 10 == 0) progress?.Report($"Перенесено файлов: {i + 1} из {names.Count}…");
        }
        progress?.Report(report.FailedAssets.Count == 0
            ? $"Готово. Перенесено файлов: {report.Assets}."
            : $"Перенесено файлов: {report.Assets}, не удалось: {report.FailedAssetNames.Count}.");
        return report;
    }

    private static MigrationReport Copy(IDocumentStore source, IDocumentStore target, HashSet<long>? selected, bool? forcePrivate, IProgress<string>? progress)
    {
        var report = new MigrationReport();
        // Целевое хранилище само создаёт всё, что ему нужно: для PostgreSQL — базу данных,
        // расширения, таблицы и индексы. Готовить схему руками не требуется.
        progress?.Report("Подготовка целевой базы: создание таблиц и индексов…");
        target.Initialize();

        progress?.Report("Перенос статусов документов…");
        foreach (var status in source.GetStatuses())
        {
            try { target.AddStatus(status); } catch (InvalidOperationException) { /* статус уже существует (встроенный или добавленный другим пользователем) */ }
        }

        var tree = source.LoadTree();
        var favorites = source.LoadFavorites().Select(x => x.Id).ToHashSet();
        var templates = source.LoadTemplates().Select(x => x.Id).ToHashSet();
        // Удалённое в корзину не переносится: LoadTree такие элементы и так не отдаёт, но явный
        // список делает правило независимым от провайдера — в общую базу попадает только то,
        // что пользователь видит в дереве документации.
        var trashed = source.LoadTrash().Select(x => x.Id).ToHashSet();
        var required = selected is null ? null : RequiredNodes(tree, selected);
        var idMap = new Dictionary<long, long>();
        var existingTitles = BuildTitleIndex(target);

        progress?.Report("Перенос дерева документов и папок…");
        void CopyNode(NodeItem node, long? newParentId)
        {
            if (trashed.Contains(node.Id)) return;
            if (required is not null && !required.Contains(node.Id)) return;
            long newId;
            try
            {
                newId = node.IsFolder
                    ? EnsureFolder(target, existingTitles, newParentId, node.Title)
                    : CopyDocument(source, target, existingTitles, node, newParentId, forcePrivate, report);
            }
            catch (Exception ex)
            {
                // Одна сбойная страница не должна обрывать перенос остальных.
                LogService.Error($"Не удалось перенести элемент «{node.Title}»", ex);
                report.FailedDocuments.Add(new(node.Title, PostgresErrorInfo.Short(ex)));
                return;
            }
            if (node.IsFolder) report.Folders++; else report.Documents++;
            if (report.Documents > 0 && report.Documents % 10 == 0) progress?.Report($"Перенесено документов: {report.Documents}…");
            idMap[node.Id] = newId;
            foreach (var child in node.Children) CopyNode(child, newId);
        }
        foreach (var root in tree) CopyNode(root, null);

        progress?.Report("Восстановление избранного и шаблонов…");
        foreach (var oldId in favorites) if (idMap.TryGetValue(oldId, out var newId)) target.ToggleFavorite(newId);
        foreach (var oldId in templates) if (idMap.TryGetValue(oldId, out var newId)) target.SetTemplate(newId, true);

        progress?.Report("Перестроение поискового индекса…");
        target.RebuildSearchIndex();
        progress?.Report(report.Summary());
        return report;
    }

    /// <summary>
    /// Переносит страницу: если она уже есть в целевой базе (совпал постоянный идентификатор
    /// страницы), содержимое обновляется прямо в ней, иначе создаётся новая — и получает
    /// идентификатор оригинала, чтобы следующая выгрузка снова обновила её, а не задвоила.
    /// </summary>
    private static long CopyDocument(IDocumentStore source, IDocumentStore target, Dictionary<(long? Parent, bool IsFolder, string Title), long> index, NodeItem node, long? newParentId, bool? forcePrivate, MigrationReport report)
    {
        var isPrivate = forcePrivate ?? source.GetDocumentAccess(node.Id).IsPrivate;
        var guid = TryGetGuid(source, node.Id);
        var existing = guid.Length > 0 ? target.FindDocumentByGuid(guid) : null;
        long newId;
        if (existing is not null)
        {
            newId = existing.Id;
            if (!string.Equals(existing.Title, node.Title, StringComparison.Ordinal))
            {
                // Заголовок мог измениться у источника; конфликт названий не критичен — оставляем прежний.
                try { target.Rename(newId, node.Title); } catch (InvalidOperationException) { }
            }
            report.Updated++;
        }
        else
        {
            newId = CreateDocument(target, index, newParentId, node.Title, isPrivate);
            if (guid.Length > 0) target.SetDocumentGuid(newId, guid);
        }
        var (document, _, _) = source.LoadDocument(node.Id);
        target.SaveDocument(newId, document, source.GetDocumentPlainText(node.Id));
        var (json, html, structuredPlainText) = source.LoadStructuredContent(node.Id);
        target.SaveStructuredContent(newId, json, html, structuredPlainText);
        target.SetDocumentMarkdown(newId, source.GetDocumentMarkdown(node.Id));
        target.SetDocumentAccess(newId, isPrivate);
        target.SetStatus(newId, source.GetStatus(node.Id));
        target.SetTags(newId, source.GetTags(node.Id));
        target.SetCustomProperties(newId, source.GetCustomProperties(node.Id));
        target.SetZoom(newId, source.GetZoom(node.Id));
        // Уже зарегистрированные вложения повторно не добавляются — иначе при каждой выгрузке
        // список вложений страницы разрастался бы копиями одних и тех же файлов.
        var registered = target.GetAttachments(newId).Select(x => x.StoredName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var attachment in source.GetAttachments(node.Id))
            if (registered.Add(attachment.StoredName))
                target.RegisterAttachment(newId, attachment.FileName, attachment.StoredName, attachment.MimeType, attachment.Size, attachment.Sha256);
        foreach (var asset in source.GetReferencedAssetNames(node.Id)) CopyAsset(source, target, asset, node.Title, report);
        return newId;
    }

    private static string TryGetGuid(IDocumentStore store, long documentId)
    {
        try { return store.GetDocumentGuid(documentId) ?? ""; }
        catch (Exception ex) { LogService.Error("Не удалось прочитать идентификатор страницы " + documentId, ex); return ""; }
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

    /// <summary>
    /// Названия занятые в целевой базе. Ключ включает тип элемента: папка и документ с одинаковым
    /// названием в одной папке — это разные элементы, и друг другу они не мешают.
    /// </summary>
    private static Dictionary<(long? Parent, bool IsFolder, string Title), long> BuildTitleIndex(IDocumentStore target)
    {
        var index = new Dictionary<(long?, bool, string), long>(TitleComparer.Instance);
        void Walk(NodeItem node)
        {
            index[(node.ParentId, node.IsFolder, node.Title)] = node.Id;
            foreach (var child in node.Children) Walk(child);
        }
        foreach (var root in target.LoadTree()) Walk(root);
        return index;
    }

    /// <summary>
    /// Папка переиспользуется, если уже есть. Если её создал параллельно другой пользователь и
    /// база отклонила вставку — папка находится в целевом хранилище и используется как своя,
    /// поэтому одновременный экспорт нескольких человек не задваивает дерево.
    /// </summary>
    private static long EnsureFolder(IDocumentStore target, Dictionary<(long? Parent, bool IsFolder, string Title), long> index, long? parentId, string title)
    {
        if (index.TryGetValue((parentId, true, title), out var existing) && existing > 0) return existing;
        try
        {
            var id = target.Create(parentId, true, title);
            index[(parentId, true, title)] = id;
            return id;
        }
        catch (InvalidOperationException ex)
        {
            var found = FindChild(target, parentId, title) ?? throw new InvalidOperationException($"Не удалось создать или найти папку «{title}» в целевой базе.", ex);
            index[(parentId, true, title)] = found;
            return found;
        }
    }

    /// <summary>
    /// Создаёт документ, подбирая свободное название. Конфликт с параллельно работающим
    /// пользователем не роняет перенос: берётся следующее свободное название, данные не теряются.
    /// </summary>
    private static long CreateDocument(IDocumentStore target, Dictionary<(long? Parent, bool IsFolder, string Title), long> index, long? parentId, string title, bool isPrivate)
    {
        var candidate = UniqueTitle(index, parentId, title);
        for (var attempt = 0; ; attempt++)
        {
            try { return target.Create(parentId, false, candidate, isPrivate); }
            catch (InvalidOperationException) when (attempt < 20)
            {
                index[(parentId, false, candidate)] = 0;
                candidate = UniqueTitle(index, parentId, title);
            }
        }
    }

    private static long? FindChild(IDocumentStore target, long? parentId, string title)
    {
        long? found = null;
        void Walk(NodeItem node)
        {
            if (found is null && node.ParentId == parentId && string.Equals(node.Title, title, StringComparison.OrdinalIgnoreCase)) found = node.Id;
            foreach (var child in node.Children) Walk(child);
        }
        foreach (var root in target.LoadTree()) Walk(root);
        return found;
    }

    private static string UniqueTitle(Dictionary<(long? Parent, bool IsFolder, string Title), long> index, long? parentId, string title)
    {
        if (!index.ContainsKey((parentId, false, title))) { index[(parentId, false, title)] = 0; return title; }
        for (var i = 2; ; i++)
        {
            var candidate = $"{title} (импорт {i})";
            if (index.ContainsKey((parentId, false, candidate))) continue;
            index[(parentId, false, candidate)] = 0;
            return candidate;
        }
    }

    /// <summary>
    /// Копирует двоичное содержимое изображения или вложения. Файл кладётся в базу (таблица
    /// Assets, ключ — имя по SHA-256, поэтому повторная выгрузка того же рисунка не создаёт
    /// второй записи) и в локальную папку Assets — кэш, из которого редактор отдаёт картинку по
    /// адресу https://assets.local/&lt;имя&gt;. Отказ базы (например, нет прав на запись) не
    /// прерывает перенос, а попадает в отчёт: файлы можно доложить отдельно.
    /// </summary>
    private static void CopyAsset(IDocumentStore source, IDocumentStore target, string storedName, string documentTitle, MigrationReport report)
    {
        try
        {
            if (target.AssetExists(storedName)) { AssetSyncService.Materialize(target, storedName); return; }
            var content = source.LoadAssetContent(storedName);
            if (content is null)
            {
                var path = AssetSyncService.LocalPath(storedName);
                if (path is null || !File.Exists(path))
                {
                    report.FailedAssets.Add(new(storedName, documentTitle, "файла нет ни в исходной базе, ни в локальной папке Assets"));
                    return;
                }
                content = (File.ReadAllBytes(path), AssetSyncService.GuessMimeType(storedName));
            }
            target.SaveAssetContent(storedName, Convert.ToHexString(SHA256.HashData(content.Value.Data)), content.Value.MimeType, content.Value.Data);
            AssetSyncService.Materialize(target, storedName);
            report.Assets++;
        }
        catch (Exception ex)
        {
            LogService.Error("Не удалось перенести файл вложения " + storedName, ex);
            report.FailedAssets.Add(new(storedName, documentTitle, PostgresErrorInfo.Short(ex)));
        }
    }

    private sealed class TitleComparer : IEqualityComparer<(long? Parent, bool IsFolder, string Title)>
    {
        public static readonly TitleComparer Instance = new();
        public bool Equals((long? Parent, bool IsFolder, string Title) x, (long? Parent, bool IsFolder, string Title) y) => x.Parent == y.Parent && x.IsFolder == y.IsFolder && string.Equals(x.Title, y.Title, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((long? Parent, bool IsFolder, string Title) obj) => HashCode.Combine(obj.Parent, obj.IsFolder, obj.Title.ToLowerInvariant());
    }
}

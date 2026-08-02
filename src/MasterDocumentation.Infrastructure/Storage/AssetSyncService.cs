using System.IO;
using System.Security.Cryptography;
using MasterDocumentation.Services;
using MasterDocumentation.Utilities;

namespace MasterDocumentation.Storage;

/// <summary>
/// Двусторонняя синхронизация файлов изображений и вложений между базой данных и локальной
/// папкой Assets. В базе файл лежит целиком (по SHA-256), локальная папка — лишь кэш, из которого
/// редактор отдаёт картинку по адресу https://assets.local/&lt;имя&gt;.
///
/// Благодаря этому изображение видно всем, кто подключился к общей базе: при открытии документа
/// недостающие файлы выгружаются из базы на диск, а при сохранении — наоборот, загружаются в базу.
/// </summary>
public static class AssetSyncService
{
    /// <summary>Кладёт файл вложения в базу данных (если его там ещё нет).</summary>
    public static void Publish(IDocumentStore store, string storedName)
    {
        if (string.IsNullOrWhiteSpace(storedName)) return;
        try
        {
            if (store.AssetExists(storedName)) return;
            var path = LocalPath(storedName);
            if (path is null || !File.Exists(path)) return;
            var bytes = File.ReadAllBytes(path);
            store.SaveAssetContent(storedName, Convert.ToHexString(SHA256.HashData(bytes)), GuessMimeType(storedName), bytes);
        }
        catch (Exception ex) { LogService.Error("Не удалось сохранить файл вложения в базу данных: " + storedName, ex); }
    }

    /// <summary>Кладёт в базу все файлы, на которые ссылается документ.</summary>
    public static void PublishDocumentAssets(IDocumentStore store, long documentId)
    {
        foreach (var name in store.GetReferencedAssetNames(documentId)) Publish(store, name);
    }

    /// <summary>Выгружает из базы на диск файл, которого нет в локальной папке Assets.</summary>
    public static bool Materialize(IDocumentStore store, string storedName)
    {
        if (string.IsNullOrWhiteSpace(storedName)) return false;
        var path = LocalPath(storedName);
        if (path is null) return false;
        if (File.Exists(path)) return true;
        try
        {
            var content = store.LoadAssetContent(storedName);
            if (content is null) return false;
            Directory.CreateDirectory(AppPaths.Assets);
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, content.Value.Data);
            File.Move(temporary, path, true);
            return true;
        }
        catch (Exception ex) { LogService.Error("Не удалось получить файл вложения из базы данных: " + storedName, ex); return false; }
    }

    /// <summary>Выгружает на диск все файлы, на которые ссылается документ.</summary>
    public static void MaterializeDocumentAssets(IDocumentStore store, long documentId)
    {
        foreach (var name in store.GetReferencedAssetNames(documentId)) Materialize(store, name);
    }

    /// <summary>Сохраняет байты файла и в локальную папку, и в базу данных.</summary>
    public static void Store(IDocumentStore store, string storedName, string sha256, string mimeType, byte[] data)
    {
        var path = LocalPath(storedName);
        if (path is not null && !File.Exists(path))
        {
            Directory.CreateDirectory(AppPaths.Assets);
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, path, true);
        }
        try { if (!store.AssetExists(storedName)) store.SaveAssetContent(storedName, sha256, mimeType, data); }
        catch (Exception ex) { LogService.Error("Не удалось сохранить файл вложения в базу данных: " + storedName, ex); }
    }

    /// <summary>Полный путь к файлу внутри папки Assets; null — если имя выходит за её пределы.</summary>
    public static string? LocalPath(string storedName)
    {
        if (string.IsNullOrWhiteSpace(storedName) || storedName.Contains('/') || storedName.Contains('\\')) return null;
        var root = Path.GetFullPath(AppPaths.Assets).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(AppPaths.Assets, storedName));
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    public static string GuessMimeType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        ".tif" or ".tiff" => "image/tiff",
        ".pdf" => "application/pdf",
        ".txt" or ".md" => "text/plain",
        ".html" or ".htm" => "text/html",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => "application/octet-stream",
    };
}

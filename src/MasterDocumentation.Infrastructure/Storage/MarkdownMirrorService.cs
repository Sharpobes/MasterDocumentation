using System.IO;
using System.Text;
using MasterDocumentation.Services;
using MasterDocumentation.Utilities;

namespace MasterDocumentation.Storage;

/// <summary>
/// Зеркало страниц документации на диске в формате Markdown (.md). Каждая страница
/// при сохранении пишется в папку Data/Documents отдельным файлом «Название (guid).md»,
/// поэтому документацию можно читать и версионировать как обычные .md-файлы, не открывая
/// приложение и не подключаясь к базе.
/// </summary>
public static class MarkdownMirrorService
{
    public static void Write(string guid, string title, string markdown)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(guid)) return;
            Directory.CreateDirectory(AppPaths.Documents);
            var suffix = Suffix(guid);
            foreach (var stale in Directory.EnumerateFiles(AppPaths.Documents, "*" + suffix + ".md")) File.Delete(stale);
            var path = Path.Combine(AppPaths.Documents, Sanitize(title) + suffix + ".md");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, markdown ?? "", new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) { LogService.Error("Не удалось обновить .md-файл документа " + title, ex); }
    }

    public static void Delete(string guid)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(guid) || !Directory.Exists(AppPaths.Documents)) return;
            foreach (var file in Directory.EnumerateFiles(AppPaths.Documents, "*" + Suffix(guid) + ".md")) File.Delete(file);
        }
        catch (Exception ex) { LogService.Error("Не удалось удалить .md-файл документа " + guid, ex); }
    }

    public static string Suffix(string guid) => " (" + new string(guid.Where(char.IsLetterOrDigit).Take(8).ToArray()) + ")";

    public static string Sanitize(string title)
    {
        var value = (title ?? "").Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '-');
        value = value.Trim('.', ' ');
        if (value.Length == 0) value = "Документ";
        return value.Length > 80 ? value[..80].TrimEnd() : value;
    }
}

using System.Text.RegularExpressions;

namespace MasterDocumentation.Storage;

/// <summary>
/// Извлечение имён файлов вложений и изображений, на которые ссылается содержимое документа.
/// Все ссылки в содержимом имеют вид https://assets.local/&lt;sha256&gt;.&lt;расширение&gt;, поэтому
/// одна и та же выборка работает и для HTML, и для JSON редактора, и для Markdown.
/// </summary>
internal static class AssetReferences
{
    public static IReadOnlyList<string> Extract(params string?[] contents)
    {
        var names = new List<string>();
        foreach (var content in contents)
        {
            if (string.IsNullOrEmpty(content)) continue;
            foreach (Match match in Regex.Matches(content, @"assets\.local/(?<name>[A-Za-z0-9._-]{1,200})"))
                names.Add(match.Groups["name"].Value);
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

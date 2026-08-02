using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MasterDocumentation.Services;

/// <summary>
/// Единственная реализация преобразования содержимого документа между HTML редактора и Markdown.
/// Markdown — канонический формат хранения страниц документации (.md): в нём страница пишется
/// в базу данных, в локальное зеркало на диске и в экспортируемые файлы. Здесь же живёт разбор
/// изображений, чтобы картинки не терялись ни при сохранении, ни при экспорте.
/// </summary>
public static class MarkdownService
{
    public const string AssetHost = "https://assets.local/";

    /// <summary>HTML редактора → Markdown. Изображения сохраняются как ![alt](src).</summary>
    public static string FromHtml(string html, string title)
    {
        var value = html ?? "";
        value = Regex.Replace(value, @"(?is)<div[^>]*data-safe-html[^>]*>(.*?)</div>", m => "\n```html\n" + WebUtility.HtmlEncode(WebUtility.HtmlDecode(StripTags(m.Groups[1].Value)).Trim()) + "\n```\n");
        value = Regex.Replace(value, @"(?is)<span(?<attributes>[^>]*data-document-anchor[^>]*)>.*?</span>", m => "\n@@MASTERDOCUMENTATION_ANCHOR:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(AttributeValue(m.Groups["attributes"].Value, "data-document-anchor"))) + "@@\n");
        value = Regex.Replace(value, @"(?is)<div[^>]*data-mermaid[^>]*>(.*?)</div>", m => "\n```mermaid\n" + WebUtility.HtmlDecode(StripTags(m.Groups[1].Value)).Trim() + "\n```\n");
        value = Regex.Replace(value, @"(?is)<span[^>]*data-formula[^>]*>(.*?)</span>", m => "$" + WebUtility.HtmlDecode(StripTags(m.Groups[1].Value)).Trim() + "$");
        value = Regex.Replace(value, @"(?is)<div[^>]*data-page-break[^>]*>\s*</div>", "\n@@MASTERDOCUMENTATION_PAGE_BREAK@@\n");
        value = ConvertFigures(value);
        value = ConvertImages(value);
        value = ConvertTables(value);
        value = Regex.Replace(value, @"(?is)<details[^>]*data-collapsible[^>]*>\s*<summary[^>]*>(.*?)</summary>\s*<div[^>]*data-details-content[^>]*>(.*?)</div>\s*</details>", m => "\n**" + Inline(m.Groups[1].Value) + "**\n\n" + Inline(m.Groups[2].Value) + "\n");
        value = Regex.Replace(value, @"(?is)<div(?<attributes>[^>]*data-callout[^>]*)>(?<body>.*?)</div>", m =>
        {
            var label = AttributeValue(m.Groups["attributes"].Value, "data-label");
            if (string.IsNullOrWhiteSpace(label)) label = "Примечание";
            return "\n> **" + WebUtility.HtmlDecode(label) + "**\n> " + Inline(m.Groups["body"].Value).Replace("\n", "\n> ") + "\n";
        });
        value = Regex.Replace(value, @"(?is)<pre[^>]*>\s*<code[^>]*>(.*?)</code>\s*</pre>", m => "\n```\n" + WebUtility.HtmlDecode(StripTags(m.Groups[1].Value)).Trim() + "\n```\n");
        value = Regex.Replace(value, @"(?is)<h([1-6])[^>]*>(.*?)</h\1>", m => "\n" + new string('#', int.Parse(m.Groups[1].Value)) + " " + Inline(m.Groups[2].Value) + "\n");
        value = Regex.Replace(value, @"(?is)<blockquote[^>]*>(.*?)</blockquote>", m => "\n> " + Inline(m.Groups[1].Value).Replace("\n", "\n> ") + "\n");
        value = Regex.Replace(value, @"(?is)<li[^>]*>(.*?)</li>", m => "\n- " + Inline(m.Groups[1].Value));
        value = Regex.Replace(value, @"(?is)<p[^>]*>(.*?)</p>", m => "\n" + Inline(m.Groups[1].Value) + "\n");
        value = Regex.Replace(value, @"(?is)<br\s*/?>", "\n");
        value = StripTags(value);
        value = WebUtility.HtmlDecode(value);
        value = value.Replace("@@MASTERDOCUMENTATION_PAGE_BREAK@@", "<div style=\"page-break-after: always;\"></div>", StringComparison.Ordinal);
        value = Regex.Replace(value, @"@@MASTERDOCUMENTATION_ANCHOR:(?<name>[A-Za-z0-9+/=]+)@@", m => "<a id=\"" + WebUtility.HtmlEncode(Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups["name"].Value))) + "\"></a>");
        value = Regex.Replace(value, @"\n{3,}", "\n\n").Trim();
        return (string.IsNullOrWhiteSpace(title) ? "" : "# " + title + "\n\n") + value + "\n";
    }

    /// <summary>Markdown → HTML редактора. Поддерживает изображения, ссылки, таблицы и списки.</summary>
    public static string ToHtml(string markdown)
    {
        var result = new StringBuilder();
        var lines = (markdown ?? "").Replace("\r", "").Split('\n');
        var inCode = false;
        var listKind = "";
        var index = 0;
        void CloseList() { if (listKind.Length == 0) return; result.Append(listKind == "ol" ? "</ol>" : "</ul>"); listKind = ""; }
        while (index < lines.Length)
        {
            var raw = lines[index];
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                CloseList();
                result.Append(inCode ? "</code></pre>" : "<pre><code>");
                inCode = !inCode;
                index++;
                continue;
            }
            if (inCode) { result.AppendLine(WebUtility.HtmlEncode(raw)); index++; continue; }

            if (Regex.IsMatch(raw, @"^\s*\|.*\|\s*$") && index + 1 < lines.Length && Regex.IsMatch(lines[index + 1], @"^\s*\|[\s:|-]+\|\s*$"))
            {
                CloseList();
                result.Append("<table><tbody>");
                var header = SplitTableRow(raw);
                result.Append("<tr>");
                foreach (var cell in header) result.Append("<th>").Append(InlineToHtml(cell)).Append("</th>");
                result.Append("</tr>");
                index += 2;
                while (index < lines.Length && Regex.IsMatch(lines[index], @"^\s*\|.*\|\s*$"))
                {
                    result.Append("<tr>");
                    foreach (var cell in SplitTableRow(lines[index])) result.Append("<td>").Append(InlineToHtml(cell)).Append("</td>");
                    result.Append("</tr>");
                    index++;
                }
                result.Append("</tbody></table>");
                continue;
            }

            var heading = Regex.Match(raw, @"^(#{1,6})\s+(.+)$");
            if (heading.Success)
            {
                CloseList();
                var level = heading.Groups[1].Value.Length;
                result.Append($"<h{level}>{InlineToHtml(heading.Groups[2].Value)}</h{level}>");
                index++;
                continue;
            }
            var quote = Regex.Match(raw, @"^\s*>\s?(.*)$");
            if (quote.Success)
            {
                CloseList();
                result.Append("<blockquote><p>").Append(InlineToHtml(quote.Groups[1].Value)).Append("</p></blockquote>");
                index++;
                continue;
            }
            var ordered = Regex.Match(raw, @"^\s*\d+[.)]\s+(.+)$");
            if (ordered.Success)
            {
                if (listKind != "ol") { CloseList(); result.Append("<ol>"); listKind = "ol"; }
                result.Append("<li>").Append(InlineToHtml(ordered.Groups[1].Value)).Append("</li>");
                index++;
                continue;
            }
            var bullet = Regex.Match(raw, @"^\s*[-*+]\s+(.+)$");
            if (bullet.Success)
            {
                if (listKind != "ul") { CloseList(); result.Append("<ul>"); listKind = "ul"; }
                result.Append("<li>").Append(InlineToHtml(bullet.Groups[1].Value)).Append("</li>");
                index++;
                continue;
            }
            CloseList();
            var standaloneImage = Regex.Match(raw.Trim(), @"^!\[(?<alt>[^\]]*)\]\((?<src>[^)\s]+)(?:\s+""(?<caption>[^""]*)"")?\)$");
            if (standaloneImage.Success)
            {
                result.Append("<p><img src=\"").Append(WebUtility.HtmlEncode(standaloneImage.Groups["src"].Value)).Append("\" alt=\"").Append(WebUtility.HtmlEncode(standaloneImage.Groups["alt"].Value)).Append("\"></p>");
                index++;
                continue;
            }
            if (Regex.IsMatch(raw, @"^\s*(<[a-zA-Z!/][^>]*>)\s*$")) { result.Append(raw.Trim()); index++; continue; }
            result.Append(string.IsNullOrWhiteSpace(raw) ? "<p></p>" : $"<p>{InlineToHtml(raw)}</p>");
            index++;
        }
        if (inCode) result.Append("</code></pre>");
        CloseList();
        return result.ToString();
    }

    /// <summary>Все адреса изображений, встречающиеся в HTML или Markdown документа.</summary>
    public static IReadOnlyList<string> ImageSources(string content)
    {
        if (string.IsNullOrEmpty(content)) return [];
        var result = new List<string>();
        foreach (Match match in Regex.Matches(content, "(?is)<img[^>]*src\\s*=\\s*[\"'](?<src>[^\"']+)[\"']"))
            result.Add(WebUtility.HtmlDecode(match.Groups["src"].Value));
        foreach (Match match in Regex.Matches(content, @"!\[[^\]]*\]\((?<src>[^)\s]+)"))
            result.Add(WebUtility.HtmlDecode(match.Groups["src"].Value));
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Имя файла вложения из ссылки вида https://assets.local/&lt;имя&gt;.</summary>
    public static string? AssetName(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        if (!source.StartsWith(AssetHost, StringComparison.OrdinalIgnoreCase)) return null;
        var name = Uri.UnescapeDataString(source[AssetHost.Length..].Split('?', '#')[0]).Trim('/');
        return name.Length == 0 || name.Contains('/') || name.Contains('\\') ? null : name;
    }

    /// <summary>Заменяет адреса изображений (в HTML и Markdown) результатом функции сопоставления.</summary>
    public static string RewriteImageSources(string content, Func<string, string?> map)
    {
        if (string.IsNullOrEmpty(content)) return content ?? "";
        content = Regex.Replace(content, "(?is)(?<prefix><img[^>]*src\\s*=\\s*[\"'])(?<src>[^\"']+)(?<suffix>[\"'])", match =>
        {
            var replacement = map(WebUtility.HtmlDecode(match.Groups["src"].Value));
            return replacement is null ? match.Value : match.Groups["prefix"].Value + WebUtility.HtmlEncode(replacement) + match.Groups["suffix"].Value;
        });
        return Regex.Replace(content, @"(?<prefix>!\[[^\]]*\]\()(?<src>[^)\s]+)", match =>
        {
            var replacement = map(WebUtility.HtmlDecode(match.Groups["src"].Value));
            return replacement is null ? match.Value : match.Groups["prefix"].Value + replacement;
        });
    }

    private static string ConvertFigures(string value) => Regex.Replace(value, @"(?is)<figure[^>]*>(?<body>.*?)</figure>", m =>
    {
        var body = m.Groups["body"].Value;
        var caption = Regex.Match(body, @"(?is)<figcaption[^>]*>(.*?)</figcaption>");
        var image = Regex.Match(body, "(?is)<img[^>]*>");
        if (!image.Success) return body;
        var markdown = ImageToMarkdown(image.Value, caption.Success ? WebUtility.HtmlDecode(StripTags(caption.Groups[1].Value)).Trim() : "");
        return "\n" + markdown + "\n";
    });

    private static string ConvertImages(string value) => Regex.Replace(value, "(?is)<img[^>]*>", m => "\n" + ImageToMarkdown(m.Value, "") + "\n");

    private static string ImageToMarkdown(string tag, string caption)
    {
        var source = AttributeValue(tag, "src");
        if (source.Length == 0) return "";
        var alt = AttributeValue(tag, "alt");
        if (alt.Length == 0) alt = caption;
        var title = caption.Length > 0 ? " \"" + caption.Replace("\"", "'") + "\"" : "";
        return $"![{WebUtility.HtmlDecode(alt).Replace("]", ")")}]({WebUtility.HtmlDecode(source)}{title})";
    }

    private static string ConvertTables(string value) => Regex.Replace(value, @"(?is)<table[^>]*>(?<body>.*?)</table>", m =>
    {
        var rows = Regex.Matches(m.Groups["body"].Value, @"(?is)<tr[^>]*>(.*?)</tr>");
        if (rows.Count == 0) return "";
        var builder = new StringBuilder("\n");
        for (var index = 0; index < rows.Count; index++)
        {
            var cells = Regex.Matches(rows[index].Groups[1].Value, @"(?is)<t[dh][^>]*>(.*?)</t[dh]>");
            builder.Append('|');
            foreach (Match cell in cells) builder.Append(' ').Append(Inline(cell.Groups[1].Value).Replace("\n", " ").Replace("|", "\\|").Trim()).Append(" |");
            builder.Append('\n');
            if (index == 0)
            {
                builder.Append('|');
                foreach (var _ in cells) builder.Append(" --- |");
                builder.Append('\n');
            }
        }
        return builder.Append('\n').ToString();
    });

    private static string[] SplitTableRow(string line)
    {
        var trimmed = line.Trim().Trim('|');
        return trimmed.Split('|').Select(x => x.Replace("\\|", "|").Trim()).ToArray();
    }

    private static string Inline(string value)
    {
        value = ConvertImages(value);
        value = Regex.Replace(value, @"(?is)<(strong|b)[^>]*>(.*?)</\1>", "**$2**");
        value = Regex.Replace(value, @"(?is)<(em|i)[^>]*>(.*?)</\1>", "*$2*");
        value = Regex.Replace(value, @"(?is)<(s|del|strike)[^>]*>(.*?)</\1>", "~~$2~~");
        value = Regex.Replace(value, @"(?is)<code[^>]*>(.*?)</code>", "`$1`");
        value = Regex.Replace(value, "(?is)<a[^>]*href=[\"']([^\"']+)[\"'][^>]*>(.*?)</a>", "[$2]($1)");
        return WebUtility.HtmlDecode(StripTags(value)).Trim('\n');
    }

    private static string InlineToHtml(string value)
    {
        var encoded = WebUtility.HtmlEncode(value);
        encoded = Regex.Replace(encoded, @"!\[(?<alt>[^\]]*)\]\((?<src>[^)\s]+)(?:\s+&quot;(?<caption>[^&]*)&quot;)?\)", m => $"<img src=\"{m.Groups["src"].Value}\" alt=\"{m.Groups["alt"].Value}\">");
        encoded = Regex.Replace(encoded, @"\[(?<text>[^\]]+)\]\((?<href>[^)\s]+)\)", m => $"<a href=\"{m.Groups["href"].Value}\">{m.Groups["text"].Value}</a>");
        encoded = Regex.Replace(encoded, @"`(?<code>[^`]+)`", m => $"<code>{m.Groups["code"].Value}</code>");
        encoded = Regex.Replace(encoded, @"\*\*(?<text>[^*]+)\*\*", m => $"<strong>{m.Groups["text"].Value}</strong>");
        encoded = Regex.Replace(encoded, @"(?<!\*)\*(?<text>[^*]+)\*(?!\*)", m => $"<em>{m.Groups["text"].Value}</em>");
        encoded = Regex.Replace(encoded, @"~~(?<text>[^~]+)~~", m => $"<s>{m.Groups["text"].Value}</s>");
        return encoded;
    }

    private static string AttributeValue(string attributes, string name)
    {
        var match = Regex.Match(attributes, Regex.Escape(name) + "\\s*=\\s*[\"'](?<value>[^\"']*)[\"']", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : "";
    }

    private static string StripTags(string value) => Regex.Replace(value, "<[^>]+>", "");
}

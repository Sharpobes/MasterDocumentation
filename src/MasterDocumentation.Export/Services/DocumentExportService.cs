using System.Net;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WpfParagraph = System.Windows.Documents.Paragraph;
using WordParagraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;
using WordRun = DocumentFormat.OpenXml.Wordprocessing.Run;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using PdfSharp.Pdf;
using PdfSharp.Drawing;

namespace MasterDocumentation.Services;

/// <summary>
/// Экспорт страницы документации. Во всех форматах сохраняется и текст, и изображения:
/// в HTML картинки встраиваются как data:URI, в Markdown копируются в папку рядом с .md,
/// в DOCX и PDF рисуются внутри самого файла. Ссылки вида https://assets.local/&lt;имя&gt;
/// разрешаются в файлы папки вложений, путь к которой передаётся в конструктор.
/// </summary>
public sealed class DocumentExportService
{
    private readonly string? _assetsFolder;

    public DocumentExportService(string? assetsFolder = null) => _assetsFolder = assetsFolder;

    public void Export(FlowDocument document, string title, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".txt": File.WriteAllText(path, PlainText(document), Encoding.UTF8); break;
            case ".md": File.WriteAllText(path, Markdown(document, title), Encoding.UTF8); break;
            case ".html": File.WriteAllText(path, Html(document, title), Encoding.UTF8); break;
            case ".docx": Docx(document, title, path); break;
            case ".pdf": PdfFromBlocks(TextBlocks(PlainText(document)), title, path); break;
            default: throw new NotSupportedException("Неподдерживаемый формат: " + ext);
        }
    }

    public void Export(string html, string plainText, string title, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".txt": File.WriteAllText(path, plainText, Encoding.UTF8); break;
            case ".html": File.WriteAllText(path, HtmlDocument(EmbedImages(html), title), Encoding.UTF8); break;
            case ".md": File.WriteAllText(path, MarkdownWithImageFiles(html, title, path), Encoding.UTF8); break;
            case ".docx": DocxFromHtml(html, title, path); break;
            case ".pdf": PdfFromBlocks(ParseBlocks(html, plainText), title, path); break;
            default: throw new NotSupportedException("Неподдерживаемый формат: " + ext);
        }
    }

    public static string PlainText(FlowDocument document) => new TextRange(document.ContentStart, document.ContentEnd).Text.TrimEnd();

    public static string Markdown(FlowDocument document, string title)
    {
        var b = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title)) b.Append("# ").AppendLine(title).AppendLine();
        foreach (var block in document.Blocks)
        {
            if (block is WpfParagraph p)
            {
                var text = new TextRange(p.ContentStart, p.ContentEnd).Text.Trim();
                var level = p.FontSize switch { >= 24 => 1, >= 20 => 2, >= 16 => 3, _ => 0 };
                if (level > 0) b.Append(new string('#', level)).Append(' ');
                b.AppendLine(text).AppendLine();
            }
            else if (block is System.Windows.Documents.List list)
            {
                var i = 1;
                foreach (var item in list.ListItems)
                {
                    var text = new TextRange(item.ContentStart, item.ContentEnd).Text.Trim();
                    b.Append(list.MarkerStyle == System.Windows.TextMarkerStyle.Decimal ? $"{i++}. " : "- ").AppendLine(text);
                }
                b.AppendLine();
            }
            else if (block is System.Windows.Documents.Table table) AppendMarkdownTable(b, table);
        }
        return b.ToString();
    }

    public static string Html(FlowDocument document, string title)
    {
        var body = WebUtility.HtmlEncode(PlainText(document)).Replace("\r\n", "<br>\n").Replace("\n", "<br>\n");
        return $"<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title><style>body{{max-width:900px;margin:40px auto;font:16px/1.6 Segoe UI,Arial;color:#222}}h1{{color:#206ea5}}</style></head><body><h1>{WebUtility.HtmlEncode(title)}</h1><p>{body}</p></body></html>";
    }

    public static string HtmlDocument(string html, string title) => """<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>""" + WebUtility.HtmlEncode(title) + """</title><style>body{max-width:920px;margin:40px auto;padding:0 24px;font:16px/1.6 "Segoe UI",Arial;color:#20242a}table{border-collapse:collapse;width:100%}td,th{border:1px solid #9aa3ad;padding:7px}pre{background:#171f29;color:#e2e7ee;padding:14px;border-radius:6px;overflow:auto}blockquote{border-left:4px solid #6e52b5;margin-left:0;padding-left:14px;color:#596273}img{max-width:100%;height:auto}figure{margin:18px 0}figcaption{color:#596273;font-size:14px}mark{padding:0 2px}</style></head><body><article>""" + html + """</article></body></html>""";

    public static string MarkdownFromHtml(string html, string title) => MarkdownService.FromHtml(html, title);

    // ---- Изображения -------------------------------------------------------

    /// <summary>Путь к файлу изображения по ссылке из содержимого документа.</summary>
    private string? ResolveImage(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var name = MarkdownService.AssetName(source);
            if (name is not null)
            {
                if (_assetsFolder is null) return null;
                var root = Path.GetFullPath(_assetsFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var candidate = Path.GetFullPath(Path.Combine(_assetsFolder, name));
                return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate) ? candidate : null;
            }
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile) return File.Exists(uri.LocalPath) ? uri.LocalPath : null;
            return File.Exists(source) ? Path.GetFullPath(source) : null;
        }
        catch { return null; }
    }

    /// <summary>Встраивает изображения в HTML как data:URI, чтобы файл открывался где угодно.</summary>
    private string EmbedImages(string html) => MarkdownService.RewriteImageSources(html, source =>
    {
        var path = ResolveImage(source);
        if (path is null) return null;
        try { return "data:" + MimeType(path) + ";base64," + Convert.ToBase64String(File.ReadAllBytes(path)); }
        catch { return null; }
    });

    /// <summary>Markdown с копиями изображений в папке «&lt;имя файла&gt;_files» рядом с .md.</summary>
    private string MarkdownWithImageFiles(string html, string title, string path)
    {
        var markdown = MarkdownService.FromHtml(html, title);
        var folderName = Path.GetFileNameWithoutExtension(path) + "_files";
        var folder = Path.Combine(Path.GetDirectoryName(path)!, folderName);
        return MarkdownService.RewriteImageSources(markdown, source =>
        {
            var file = ResolveImage(source);
            if (file is null) return null;
            try
            {
                Directory.CreateDirectory(folder);
                var target = Path.Combine(folder, Path.GetFileName(file));
                if (!File.Exists(target)) File.Copy(file, target);
                return folderName + "/" + Uri.EscapeDataString(Path.GetFileName(file));
            }
            catch { return null; }
        });
    }

    private static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };

    private static (int Width, int Height) ImageSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch { return (800, 600); }
    }

    // ---- Разбор содержимого на блоки ---------------------------------------

    private enum BlockKind { Paragraph, Heading, Quote, Code, ListItem, Image, TableRow }

    private sealed record ExportBlock(BlockKind Kind, string Text, int Level = 0, string? ImagePath = null);

    private static IReadOnlyList<ExportBlock> TextBlocks(string text) =>
        text.Replace("\r", "").Split('\n').Select(line => new ExportBlock(BlockKind.Paragraph, line)).ToList();

    /// <summary>
    /// Разбор HTML документа на последовательность блоков для отрисовки в PDF.
    /// Если разметка пустая, используется обычный текст страницы — так документ никогда
    /// не экспортируется «пустым».
    /// </summary>
    private IReadOnlyList<ExportBlock> ParseBlocks(string html, string plainText)
    {
        var blocks = new List<ExportBlock>();
        var value = PrepareForBlocks(html ?? "");
        foreach (Match match in Regex.Matches(value, @"(?is)<(?<tag>h[1-6]|p|blockquote|pre|li|table)\b[^>]*>(?<body>.*?)</\k<tag>>|(?<image><img\b[^>]*>)"))
        {
            if (match.Groups["image"].Success)
            {
                AddImage(blocks, match.Groups["image"].Value);
                continue;
            }
            var tag = match.Groups["tag"].Value.ToLowerInvariant();
            var body = match.Groups["body"].Value;
            if (tag == "table")
            {
                foreach (Match row in Regex.Matches(body, @"(?is)<tr\b[^>]*>(.*?)</tr>"))
                {
                    var cells = Regex.Matches(row.Groups[1].Value, @"(?is)<t[dh]\b[^>]*>(.*?)</t[dh]>").Select(x => InlineText(x.Groups[1].Value)).ToArray();
                    if (cells.Length > 0) blocks.Add(new(BlockKind.TableRow, string.Join("  |  ", cells)));
                }
                continue;
            }
            foreach (Match image in Regex.Matches(body, "(?is)<img\\b[^>]*>")) AddImage(blocks, image.Value);
            var text = tag == "pre" ? WebUtility.HtmlDecode(Regex.Replace(body, "<[^>]+>", "")).TrimEnd() : InlineText(body);
            if (text.Length == 0) continue;
            blocks.Add(tag switch
            {
                "blockquote" => new(BlockKind.Quote, text),
                "pre" => new(BlockKind.Code, text),
                "li" => new(BlockKind.ListItem, text),
                _ when tag.StartsWith('h') => new(BlockKind.Heading, text, int.Parse(tag[1..])),
                _ => new(BlockKind.Paragraph, text),
            });
        }
        if (blocks.Count == 0 && !string.IsNullOrWhiteSpace(plainText)) return TextBlocks(plainText);
        return blocks;
    }

    private void AddImage(List<ExportBlock> blocks, string tag)
    {
        var source = WebUtility.HtmlDecode(AttributeValue(tag, "src"));
        var alt = WebUtility.HtmlDecode(AttributeValue(tag, "alt"));
        var path = ResolveImage(source);
        if (path is not null) blocks.Add(new(BlockKind.Image, alt, 0, path));
        else if (alt.Length > 0) blocks.Add(new(BlockKind.Paragraph, "[изображение: " + alt + "]"));
    }

    /// <summary>Разворачивает специальные блоки редактора в обычную разметку.</summary>
    private static string PrepareForBlocks(string html)
    {
        html = Regex.Replace(html, @"(?is)<div[^>]*data-safe-html[^>]*>(.*?)</div>", m => "<pre>" + m.Groups[1].Value + "</pre>");
        html = Regex.Replace(html, @"(?is)<div[^>]*data-mermaid[^>]*>(.*?)</div>", m => "<pre>" + m.Groups[1].Value + "</pre>");
        html = Regex.Replace(html, @"(?is)<span[^>]*data-document-anchor[^>]*>.*?</span>", "");
        html = Regex.Replace(html, @"(?is)<div[^>]*data-page-break[^>]*>\s*</div>", "");
        html = Regex.Replace(html, @"(?is)<figcaption[^>]*>(.*?)</figcaption>", m => "<p>" + m.Groups[1].Value + "</p>");
        html = Regex.Replace(html, @"(?is)<details[^>]*data-collapsible[^>]*>\s*<summary[^>]*>(.*?)</summary>\s*<div[^>]*data-details-content[^>]*>(.*?)</div>\s*</details>", "<p><strong>$1</strong></p>$2");
        html = Regex.Replace(html, @"(?is)<div(?<attributes>[^>]*data-callout[^>]*)>(?<body>.*?)</div>", m => "<blockquote>" + m.Groups["body"].Value + "</blockquote>");
        return html;
    }

    private static string InlineText(string fragment) => WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(fragment, @"(?is)<br\s*/?>", " "), "<[^>]+>", "")).Replace('\u00a0', ' ').Trim();

    // ---- PDF ---------------------------------------------------------------

    /// <summary>Заголовок документа с местом, куда ведёт навигация: страница и отступ сверху.</summary>
    private sealed record PdfHeading(int Level, string Text, PdfPage Page, int PageIndex, double Top);

    private static void PdfFromBlocks(IReadOnlyList<ExportBlock> blocks, string title, string path)
    {
        const double margin = 48;
        var pdf = new PdfDocument();
        pdf.Info.Title = title;
        var page = pdf.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        var gfx = XGraphics.FromPdfPage(page);
        var width = page.Width.Point - margin * 2;
        var body = new XFont("Segoe UI", 11);
        var code = new XFont("Consolas", 10);
        var titleFont = new XFont("Segoe UI", 20, XFontStyleEx.Bold);
        var headings = new List<PdfHeading>();
        var y = margin;

        void NewPage()
        {
            gfx.Dispose();
            page = pdf.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            gfx = XGraphics.FromPdfPage(page);
            y = margin;
        }
        void Reserve(double height)
        {
            if (y + height > page.Height.Point - margin) NewPage();
        }
        void DrawLines(string text, XFont font, double indent, XBrush brush)
        {
            var lineHeight = font.GetHeight() * 1.18;
            foreach (var line in WrapLines(gfx, text, font, width - indent))
            {
                Reserve(lineHeight);
                gfx.DrawString(line, font, brush, new XRect(margin + indent, y, width - indent, lineHeight), XStringFormats.TopLeft);
                y += lineHeight;
            }
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            DrawLines(title, titleFont, 0, XBrushes.Black);
            y += 10;
        }

        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case BlockKind.Heading:
                    {
                        var size = block.Level switch { 1 => 17d, 2 => 15d, 3 => 13.5, _ => 12d };
                        var font = new XFont("Segoe UI", size, XFontStyleEx.Bold);
                        y += 8;
                        // Место заголовка фиксируется после возможного перехода на новую страницу,
                        // иначе закладка укажет на конец предыдущей.
                        Reserve(font.GetHeight() * 1.18);
                        headings.Add(new(block.Level, block.Text, page, pdf.PageCount - 1, y));
                        DrawLines(block.Text, font, 0, XBrushes.Black);
                        y += 4;
                        break;
                    }
                case BlockKind.Quote:
                    DrawLines(block.Text, new XFont("Segoe UI", 11, XFontStyleEx.Italic), 18, XBrushes.DimGray);
                    y += 5;
                    break;
                case BlockKind.Code:
                    foreach (var line in block.Text.Replace("\r", "").Split('\n')) DrawLines(line.Length == 0 ? " " : line, code, 12, XBrushes.Black);
                    y += 6;
                    break;
                case BlockKind.ListItem:
                    DrawLines("•  " + block.Text, body, 14, XBrushes.Black);
                    break;
                case BlockKind.TableRow:
                    DrawLines(block.Text, body, 8, XBrushes.Black);
                    break;
                case BlockKind.Image:
                    DrawImage(block);
                    break;
                default:
                    if (block.Text.Trim().Length == 0) { y += 6; break; }
                    DrawLines(block.Text, body, 0, XBrushes.Black);
                    y += 6;
                    break;
            }
        }

        void DrawImage(ExportBlock block)
        {
            if (block.ImagePath is null) return;
            try
            {
                using var image = XImage.FromFile(block.ImagePath);
                var scale = Math.Min(1, width / image.PointWidth);
                var drawWidth = image.PointWidth * scale;
                var drawHeight = image.PointHeight * scale;
                var maxHeight = page.Height.Point - margin * 2;
                if (drawHeight > maxHeight)
                {
                    drawWidth *= maxHeight / drawHeight;
                    drawHeight = maxHeight;
                }
                Reserve(drawHeight);
                gfx.DrawImage(image, margin, y, drawWidth, drawHeight);
                // Альтернативный текст под картинкой не рисуется: это описание для программ
                // чтения с экрана, а не подпись. Настоящая подпись приходит отдельным абзацем.
                y += drawHeight + 12;
            }
            catch
            {
                DrawLines("[изображение недоступно: " + Path.GetFileName(block.ImagePath) + "]", body, 0, XBrushes.DimGray);
            }
        }

        gfx.Dispose();
        BuildNavigation(pdf, headings, margin);
        pdf.Save(path);
    }

    /// <summary>
    /// Навигация по документу: закладки PDF и страница оглавления со ссылками. Оглавление
    /// вставляется в начало уже свёрстанного документа, поэтому номера страниц заголовков
    /// сдвигаются на его размер; закладки ссылаются на сами объекты страниц и от вставки
    /// не зависят.
    /// </summary>
    private static void BuildNavigation(PdfDocument pdf, IReadOnlyList<PdfHeading> headings, double margin)
    {
        if (headings.Count == 0) return;

        var entryFont = new XFont("Segoe UI", 11);
        var tocTitleFont = new XFont("Segoe UI", 16, XFontStyleEx.Bold);
        var lineHeight = entryFont.GetHeight() * 1.55;
        var pageHeight = pdf.Pages[0].Height.Point;
        var pageWidth = pdf.Pages[0].Width.Point;
        var usableHeight = pageHeight - margin * 2;
        var firstPageHeight = usableHeight - tocTitleFont.GetHeight() * 1.18 - 12;
        var perPage = Math.Max(1, (int)(usableHeight / lineHeight));
        var onFirstPage = Math.Max(1, (int)(firstPageHeight / lineHeight));
        var tocPageCount = headings.Count <= onFirstPage ? 1 : 1 + (int)Math.Ceiling((headings.Count - onFirstPage) / (double)perPage);

        var tocPages = new List<PdfPage>();
        for (var index = 0; index < tocPageCount; index++)
        {
            var tocPage = pdf.Pages.Insert(index);
            tocPage.Size = PdfSharp.PageSize.A4;
            tocPages.Add(tocPage);
        }

        var entry = 0;
        for (var index = 0; index < tocPages.Count && entry < headings.Count; index++)
        {
            var tocPage = tocPages[index];
            using var gfx = XGraphics.FromPdfPage(tocPage);
            var y = margin;
            if (index == 0)
            {
                gfx.DrawString("Оглавление", tocTitleFont, XBrushes.Black, new XRect(margin, y, pageWidth - margin * 2, tocTitleFont.GetHeight() * 1.18), XStringFormats.TopLeft);
                y += tocTitleFont.GetHeight() * 1.18 + 12;
            }
            while (entry < headings.Count && y + lineHeight <= pageHeight - margin)
            {
                var heading = headings[entry++];
                var indent = Math.Min(4, Math.Max(1, heading.Level) - 1) * 16d;
                var number = (tocPageCount + heading.PageIndex + 1).ToString();
                var numberWidth = gfx.MeasureString(number, entryFont).Width;
                var textLeft = margin + indent;
                var textWidth = pageWidth - margin - numberWidth - 10 - textLeft;
                gfx.DrawString(Ellipsize(gfx, heading.Text, entryFont, textWidth), entryFont, XBrushes.Black, new XRect(textLeft, y, textWidth, lineHeight), XStringFormats.TopLeft);
                gfx.DrawString(number, entryFont, XBrushes.Black, new XRect(pageWidth - margin - numberWidth, y, numberWidth, lineHeight), XStringFormats.TopLeft);
                // Прямоугольник ссылки задаётся в координатах PDF: начало отсчёта — левый нижний угол.
                var rect = new PdfRectangle(new XPoint(textLeft, pageHeight - y - lineHeight), new XPoint(pageWidth - margin, pageHeight - y));
                tocPage.AddDocumentLink(rect, tocPageCount + heading.PageIndex + 1);
                y += lineHeight;
            }
        }

        pdf.PageMode = PdfPageMode.UseOutlines;
        var opened = pdf.Outlines.Add("Оглавление", tocPages[0], true);
        opened.PageDestinationType = PdfPageDestinationType.Fit;
        var stack = new List<(int Level, PdfOutline Outline)>();
        foreach (var heading in headings)
        {
            while (stack.Count > 0 && stack[^1].Level >= heading.Level) stack.RemoveAt(stack.Count - 1);
            var collection = stack.Count == 0 ? pdf.Outlines : stack[^1].Outline.Outlines;
            var outline = collection.Add(heading.Text, heading.Page, true);
            outline.PageDestinationType = PdfPageDestinationType.FitH;
            outline.Top = heading.Page.Height.Point - heading.Top;
            stack.Add((heading.Level, outline));
        }
    }

    private static string Ellipsize(XGraphics gfx, string text, XFont font, double width)
    {
        text = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (width <= 0 || gfx.MeasureString(text, font).Width <= width) return text;
        var take = text.Length;
        while (take > 1 && gfx.MeasureString(text[..take] + "…", font).Width > width) take--;
        return text[..take] + "…";
    }

    private static IEnumerable<string> WrapLines(XGraphics gfx, string text, XFont font, double width)
    {
        foreach (var paragraph in (text ?? "").Replace("\r", "").Split('\n'))
        {
            if (paragraph.Trim().Length == 0) { yield return ""; continue; }
            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (gfx.MeasureString(candidate, font).Width <= width) { line.Clear().Append(candidate); continue; }
                if (line.Length > 0) { yield return line.ToString(); line.Clear(); }
                var rest = word;
                while (gfx.MeasureString(rest, font).Width > width && rest.Length > 1)
                {
                    var take = rest.Length;
                    while (take > 1 && gfx.MeasureString(rest[..take], font).Width > width) take--;
                    yield return rest[..take];
                    rest = rest[take..];
                }
                line.Append(rest);
            }
            if (line.Length > 0) yield return line.ToString();
        }
    }

    // ---- DOCX --------------------------------------------------------------

    private void DocxFromHtml(string html, string title, string path)
    {
        using var word = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = word.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);
        if (!string.IsNullOrWhiteSpace(title))
            body.Append(new WordParagraph(new ParagraphProperties(new ParagraphStyleId { Val = "Title" }), new WordRun(new RunProperties(new DocumentFormat.OpenXml.Wordprocessing.Bold(), new FontSize { Val = "32" }), new Text(title))));
        var imageIndex = 0U;
        foreach (var block in ParseBlocks(html, ""))
        {
            switch (block.Kind)
            {
                case BlockKind.Image when block.ImagePath is not null:
                    // Альтернативный текст уходит в свойства рисунка, а не в отдельный абзац.
                    var picture = CreateImageParagraph(main, block.ImagePath, block.Text, ++imageIndex);
                    if (picture is not null) body.Append(picture);
                    break;
                case BlockKind.Heading:
                    body.Append(CreateWordParagraph(WebUtility.HtmlEncode(block.Text), "Heading" + Math.Clamp(block.Level, 1, 6)));
                    break;
                case BlockKind.Quote:
                    {
                        var quote = CreateWordParagraph(WebUtility.HtmlEncode(block.Text), null);
                        quote.ParagraphProperties!.Append(new Indentation { Left = "480" });
                        body.Append(quote);
                        break;
                    }
                case BlockKind.Code:
                    {
                        var pre = CreateWordParagraph(WebUtility.HtmlEncode(block.Text).Replace("\n", "<br/>"), null);
                        pre.ParagraphProperties!.Append(new Shading { Fill = "E8ECF1" });
                        body.Append(pre);
                        break;
                    }
                case BlockKind.ListItem:
                    body.Append(CreateWordParagraph("• " + WebUtility.HtmlEncode(block.Text), null));
                    break;
                default:
                    body.Append(CreateWordParagraph(WebUtility.HtmlEncode(block.Text), null));
                    break;
            }
        }
        main.Document.Save();
    }

    private static WordParagraph? CreateImageParagraph(MainDocumentPart main, string imagePath, string alt, uint index)
    {
        try
        {
            var extension = Path.GetExtension(imagePath).ToLowerInvariant();
            var type = extension switch
            {
                ".png" => ImagePartType.Png,
                ".gif" => ImagePartType.Gif,
                ".bmp" => ImagePartType.Bmp,
                ".tif" or ".tiff" => ImagePartType.Tiff,
                _ => ImagePartType.Jpeg,
            };
            var part = main.AddImagePart(type);
            using (var stream = File.OpenRead(imagePath)) part.FeedData(stream);
            var relationship = main.GetIdOfPart(part);
            var (pixelWidth, pixelHeight) = ImageSize(imagePath);
            const long maxWidthEmu = 5486400L; // ширина текстового блока страницы A4
            var widthEmu = pixelWidth * 9525L;
            var heightEmu = pixelHeight * 9525L;
            if (widthEmu > maxWidthEmu)
            {
                heightEmu = (long)(heightEmu * (maxWidthEmu / (double)widthEmu));
                widthEmu = maxWidthEmu;
            }
            var drawing = new DocumentFormat.OpenXml.Wordprocessing.Drawing(
                new DW.Inline(
                    new DW.Extent { Cx = widthEmu, Cy = heightEmu },
                    new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                    new DW.DocProperties { Id = index, Name = string.IsNullOrWhiteSpace(alt) ? "Изображение " + index : alt },
                    new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                    new A.Graphic(new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = 0U, Name = Path.GetFileName(imagePath) },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relationship },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = widthEmu, Cy = heightEmu }),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }))
                        )
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
                { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U });
            return new WordParagraph(new ParagraphProperties(), new WordRun(drawing));
        }
        catch { return null; }
    }

    private static WordParagraph CreateWordParagraph(string fragment, string? style)
    {
        var properties = new ParagraphProperties();
        if (style is not null) properties.Append(new ParagraphStyleId { Val = style });
        var paragraph = new WordParagraph(properties);
        var bold = false; var italic = false; var underline = false; var code = false;
        foreach (Match token in Regex.Matches(fragment, @"(?is)<(/?)(strong|b|em|i|u|code|br)\b[^>]*>|([^<]+)"))
        {
            if (token.Groups[3].Success)
            {
                var text = WebUtility.HtmlDecode(token.Groups[3].Value);
                if (text.Length == 0) continue;
                var rp = new RunProperties();
                if (bold) rp.Append(new DocumentFormat.OpenXml.Wordprocessing.Bold());
                if (italic) rp.Append(new DocumentFormat.OpenXml.Wordprocessing.Italic());
                if (underline) rp.Append(new DocumentFormat.OpenXml.Wordprocessing.Underline { Val = UnderlineValues.Single });
                if (code) rp.Append(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" });
                paragraph.Append(new WordRun(rp, new Text(text) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }));
                continue;
            }
            var closing = token.Groups[1].Value == "/";
            switch (token.Groups[2].Value.ToLowerInvariant())
            {
                case "strong": case "b": bold = !closing; break;
                case "em": case "i": italic = !closing; break;
                case "u": underline = !closing; break;
                case "code": code = !closing; break;
                case "br": paragraph.Append(new WordRun(new Break())); break;
            }
        }
        return paragraph;
    }

    private static void Docx(FlowDocument document, string title, string path)
    {
        using var word = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = word.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);
        if (!string.IsNullOrWhiteSpace(title)) body.Append(new WordParagraph(new ParagraphProperties(new ParagraphStyleId { Val = "Title" }), new WordRun(new RunProperties(new DocumentFormat.OpenXml.Wordprocessing.Bold(), new FontSize { Val = "32" }), new Text(title))));
        foreach (var block in document.Blocks)
        {
            if (block is WpfParagraph p)
            {
                var text = new TextRange(p.ContentStart, p.ContentEnd).Text.TrimEnd();
                var props = new ParagraphProperties();
                if (p.FontSize >= 24) props.Append(new ParagraphStyleId { Val = "Heading1" });
                else if (p.FontSize >= 20) props.Append(new ParagraphStyleId { Val = "Heading2" });
                var runProps = new RunProperties();
                if (p.FontWeight == System.Windows.FontWeights.Bold) runProps.Append(new DocumentFormat.OpenXml.Wordprocessing.Bold());
                if (p.FontStyle == System.Windows.FontStyles.Italic) runProps.Append(new DocumentFormat.OpenXml.Wordprocessing.Italic());
                body.Append(new WordParagraph(props, new WordRun(runProps, new Text(text) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve })));
            }
            else if (block is System.Windows.Documents.Table table) body.Append(ToWordTable(table));
        }
        main.Document.Save();
    }

    private static DocumentFormat.OpenXml.Wordprocessing.Table ToWordTable(System.Windows.Documents.Table source)
    {
        var table = new DocumentFormat.OpenXml.Wordprocessing.Table(new TableProperties(new TableBorders(new TopBorder { Val = BorderValues.Single }, new BottomBorder { Val = BorderValues.Single }, new LeftBorder { Val = BorderValues.Single }, new RightBorder { Val = BorderValues.Single }, new InsideHorizontalBorder { Val = BorderValues.Single }, new InsideVerticalBorder { Val = BorderValues.Single })));
        foreach (var group in source.RowGroups)
            foreach (var row in group.Rows)
            {
                var tr = new DocumentFormat.OpenXml.Wordprocessing.TableRow();
                foreach (var cell in row.Cells) tr.Append(new DocumentFormat.OpenXml.Wordprocessing.TableCell(new WordParagraph(new WordRun(new Text(new TextRange(cell.ContentStart, cell.ContentEnd).Text.Trim())))));
                table.Append(tr);
            }
        return table;
    }

    private static void AppendMarkdownTable(StringBuilder b, System.Windows.Documents.Table table)
    {
        var rows = table.RowGroups.SelectMany(g => g.Rows).ToList();
        if (rows.Count == 0) return;
        for (var r = 0; r < rows.Count; r++)
        {
            b.Append('|');
            foreach (var cell in rows[r].Cells) b.Append(' ').Append(new TextRange(cell.ContentStart, cell.ContentEnd).Text.Trim().Replace("|", "\\|")).Append(" |");
            b.AppendLine();
            if (r == 0)
            {
                b.Append('|');
                foreach (var _ in rows[r].Cells) b.Append(" --- |");
                b.AppendLine();
            }
        }
        b.AppendLine();
    }

    private static string AttributeValue(string attributes, string name)
    {
        var match = Regex.Match(attributes, Regex.Escape(name) + "\\s*=\\s*[\"'](?<value>[^\"']*)[\"']", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : "";
    }
}

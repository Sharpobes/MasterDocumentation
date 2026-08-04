using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace MasterDocumentation.Services;

public enum PdfBlockKind
{
    Text,
    Image,
}

/// <summary>Строка текста или изображение страницы вместе с положением по вертикали.</summary>
public sealed record PdfBlock(PdfBlockKind Kind, string Text, byte[]? Image, string Extension);

/// <summary>
/// Импорт PDF с содержимым, а не ссылкой на файл. PDF хранит команды рисования, а шрифты в нём
/// обычно встроены со своей кодировкой, поэтому текст читается библиотекой, умеющей карты
/// Unicode. Картинки извлекаются отдельно и расставляются между строками по положению на
/// странице. У сканов текстового слоя нет — там остаются только изображения.
/// </summary>
public static class PdfImportService
{
    public sealed record PdfImportResult(IReadOnlyList<IReadOnlyList<PdfBlock>> Pages, int PageCount)
    {
        public bool HasText => Pages.Any(page => page.Any(block => block.Kind == PdfBlockKind.Text));
        public bool HasImages => Pages.Any(page => page.Any(block => block.Kind == PdfBlockKind.Image));
        public bool HasContent => HasText || HasImages;

        public string PlainText => string.Join("\n\n", Pages
            .Select(page => string.Join("\n", page.Where(block => block.Kind == PdfBlockKind.Text).Select(block => block.Text)))
            .Where(page => page.Length > 0));
    }

    public static PdfImportResult Extract(string path)
    {
        using var document = PdfDocument.Open(path);
        var pages = new List<IReadOnlyList<PdfBlock>>();
        foreach (var page in document.GetPages())
        {
            try { pages.Add(ReadPage(page)); }
            catch { pages.Add([]); } // повреждённая страница не должна срывать импорт остальных
        }
        return new(pages, pages.Count);
    }

    private static IReadOnlyList<PdfBlock> ReadPage(Page page)
    {
        var items = new List<(double Top, PdfBlock Block)>();
        foreach (var (top, text) in ReadLines(page)) items.Add((top, new(PdfBlockKind.Text, text, null, "")));
        foreach (var (top, bytes, extension) in ReadImages(page)) items.Add((top, new(PdfBlockKind.Image, "", bytes, extension)));
        // Начало координат PDF — левый нижний угол, поэтому сверху идут блоки с большим Y.
        return items.OrderByDescending(item => item.Top).Select(item => item.Block).ToList();
    }

    /// <summary>
    /// Слова собираются в строки по положению: порядок команд рисования в PDF не обязан совпадать
    /// с порядком чтения, а переносов строк отдельными символами там нет.
    /// </summary>
    private static IEnumerable<(double Top, string Text)> ReadLines(Page page)
    {
        var words = page.GetWords(NearestNeighbourWordExtractor.Instance)
            .Where(word => word.Text.Trim().Length > 0)
            .OrderByDescending(word => word.BoundingBox.Bottom)
            .ThenBy(word => word.BoundingBox.Left)
            .ToList();

        var line = new StringBuilder();
        double lineTop = 0;
        double? previousBottom = null;
        foreach (var word in words)
        {
            var bottom = word.BoundingBox.Bottom;
            var height = Math.Max(1, word.BoundingBox.Height);
            if (previousBottom is not null && Math.Abs(previousBottom.Value - bottom) > height * 0.6)
            {
                if (line.Length > 0) yield return (lineTop, Squeeze(line.ToString()));
                line.Clear();
            }
            if (line.Length == 0) lineTop = word.BoundingBox.Top;
            else line.Append(' ');
            line.Append(word.Text.Trim());
            previousBottom = bottom;
        }
        if (line.Length > 0) yield return (lineTop, Squeeze(line.ToString()));
    }

    private static IEnumerable<(double Top, byte[] Bytes, string Extension)> ReadImages(Page page)
    {
        foreach (var image in page.GetImages())
        {
            byte[]? bytes = null;
            var extension = ".png";
            try
            {
                // JPEG внутри PDF лежит как есть — его выгоднее сохранить без перекодирования.
                var raw = image.RawBytes.ToArray();
                if (raw.Length > 3 && raw[0] == 0xFF && raw[1] == 0xD8) { bytes = raw; extension = ".jpg"; }
                else if (image.TryGetPng(out var png) && png is { Length: > 0 }) bytes = png;
            }
            catch { bytes = null; }
            if (bytes is null || bytes.Length == 0) continue;
            yield return (image.Bounds.Top, bytes, extension);
        }
    }

    private static string Squeeze(string value)
    {
        var text = value.Trim();
        while (text.Contains("  ", StringComparison.Ordinal)) text = text.Replace("  ", " ");
        return text;
    }
}

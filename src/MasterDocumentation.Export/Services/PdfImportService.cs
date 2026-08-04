using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace MasterDocumentation.Services;

/// <summary>
/// Импорт PDF с содержимым, а не ссылкой на файл. PDF хранит команды рисования, а не текст,
/// и шрифты в нём обычно встроены со своей кодировкой, поэтому разбор ведётся библиотекой,
/// умеющей читать карты Unicode. У сканов текстового слоя нет — такой файл остаётся вложением.
/// </summary>
public static class PdfImportService
{
    public sealed record PdfImportResult(IReadOnlyList<string> Pages, int PageCount)
    {
        public bool HasText => Pages.Any(page => page.Length > 0);
        public string PlainText => string.Join("\n\n", Pages.Where(page => page.Length > 0));
    }

    public static PdfImportResult Extract(string path)
    {
        using var document = PdfDocument.Open(path);
        var pages = new List<string>();
        foreach (var page in document.GetPages())
        {
            try { pages.Add(ReadPage(page)); }
            catch { pages.Add(""); } // повреждённая страница не должна срывать импорт остальных
        }
        return new(pages, pages.Count);
    }

    /// <summary>
    /// Слова собираются с учётом расположения на странице: порядок команд рисования в PDF
    /// не обязан совпадать с порядком чтения, а переносы строк отдельными символами не хранятся.
    /// </summary>
    private static string ReadPage(Page page)
    {
        var words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToList();
        if (words.Count == 0) return "";

        var builder = new StringBuilder();
        double? previousBottom = null;
        double? previousRight = null;
        foreach (var word in words.OrderByDescending(word => word.BoundingBox.Bottom).ThenBy(word => word.BoundingBox.Left))
        {
            var text = word.Text.Trim();
            if (text.Length == 0) continue;
            var bottom = word.BoundingBox.Bottom;
            var height = Math.Max(1, word.BoundingBox.Height);
            if (previousBottom is null) { }
            else if (Math.Abs(previousBottom.Value - bottom) > height * 0.6) builder.Append('\n');
            else if (previousRight is not null && word.BoundingBox.Left - previousRight.Value > height * 0.3) builder.Append(' ');
            else builder.Append(' ');
            builder.Append(text);
            previousBottom = bottom;
            previousRight = word.BoundingBox.Right;
        }
        return Normalize(builder.ToString());
    }

    private static string Normalize(string value)
    {
        var result = new List<string>();
        foreach (var line in value.Replace("\r", "").Split('\n'))
        {
            var trimmed = line.Trim();
            while (trimmed.Contains("  ", StringComparison.Ordinal)) trimmed = trimmed.Replace("  ", " ");
            if (trimmed.Length == 0)
            {
                if (result.Count > 0 && result[^1].Length > 0) result.Add("");
                continue;
            }
            result.Add(trimmed);
        }
        while (result.Count > 0 && result[^1].Length == 0) result.RemoveAt(result.Count - 1);
        return string.Join("\n", result);
    }
}

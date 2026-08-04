using MasterDocumentation.Services;
using MasterDocumentation.Utilities;

namespace MasterDocumentation.Tests;

/// <summary>
/// Импорт PDF переносит содержимое, а не ссылку на файл. Проверка выполняется круговым
/// прогоном: документ экспортируется в PDF и читается обратно.
/// </summary>
public sealed class PdfImportTests : IDisposable
{
    private readonly string _folder = Path.Combine(AppPaths.Temp, "pdf-import-tests");

    public PdfImportTests()
    {
        AppPaths.Ensure();
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch { /* временная папка */ }
    }

    [Fact]
    public void Extract_ReadsTextOfExportedDocument()
    {
        var path = Path.Combine(_folder, "document.pdf");
        new DocumentExportService().Export(
            "<h1>Раздел первый</h1><p>Текст первого абзаца документа.</p><h2>Подраздел</h2><p>Второй абзац со словами.</p>",
            "запасной текст",
            "Проверка импорта",
            path);

        var result = PdfImportService.Extract(path);

        Assert.True(result.HasText);
        Assert.Contains("Раздел первый", result.PlainText);
        Assert.Contains("Текст первого абзаца документа.", result.PlainText);
        Assert.Contains("Второй абзац со словами.", result.PlainText);
    }

    /// <summary>Страницы возвращаются по отдельности: импорт помечает их в документе.</summary>
    [Fact]
    public void Extract_KeepsPagesSeparate()
    {
        var path = Path.Combine(_folder, "long.pdf");
        var html = new System.Text.StringBuilder();
        for (var index = 1; index <= 60; index++) html.Append("<p>Строка номер ").Append(index).Append(" для заполнения страницы.</p>");
        new DocumentExportService().Export(html.ToString(), "запас", "Длинный документ", path);

        var result = PdfImportService.Extract(path);

        Assert.True(result.PageCount > 1);
        Assert.True(result.HasText);
        Assert.Contains("Строка номер 60", result.PlainText);
    }

    /// <summary>Пустой PDF без текстового слоя импортом содержимого не считается.</summary>
    [Fact]
    public void Extract_ReportsMissingTextLayer()
    {
        var path = Path.Combine(_folder, "empty.pdf");
        var document = new PdfSharp.Pdf.PdfDocument();
        document.AddPage();
        document.Save(path);

        var result = PdfImportService.Extract(path);

        Assert.False(result.HasText);
        Assert.Equal(1, result.PageCount);
    }
}

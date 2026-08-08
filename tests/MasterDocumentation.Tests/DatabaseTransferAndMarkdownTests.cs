using MasterDocumentation.Models;
using MasterDocumentation.Services;
using MasterDocumentation.Storage;
using MasterDocumentation.Utilities;

namespace MasterDocumentation.Tests;

/// <summary>
/// Хранение страниц в Markdown, изображения в базе данных и в экспорте, приватные страницы
/// и выборочный обмен документацией с базой данных.
/// </summary>
public sealed class DatabaseTransferAndMarkdownTests : IDisposable
{
    // Минимальное корректное изображение PNG 1×1 — достаточно для проверки переноса и экспорта.
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    private readonly string _originalUser = UserIdentity.Current;

    public DatabaseTransferAndMarkdownTests()
    {
        if (Directory.Exists(AppPaths.Data)) Directory.Delete(AppPaths.Data, true);
        AppPaths.Ensure();
    }

    [Fact]
    public void PageIsStoredAsMarkdownAndMirroredToMdFile()
    {
        var database = new DatabaseService();
        database.Initialize();
        var id = database.Create(null, false, "Страница документации");
        database.SaveStructuredContent(id, "{\"type\":\"doc\"}", "<h1>Раздел</h1><p>Текст страницы</p>", "Раздел Текст страницы");

        var markdown = database.GetDocumentMarkdown(id);
        Assert.Contains("# Страница документации", markdown);
        Assert.Contains("# Раздел", markdown);
        Assert.Contains("Текст страницы", markdown);

        var file = Assert.Single(Directory.EnumerateFiles(AppPaths.Documents, "*.md"));
        Assert.StartsWith("Страница документации", Path.GetFileName(file));
        Assert.Contains("Текст страницы", File.ReadAllText(file));
    }

    [Fact]
    public void MarkdownKeepsImagesInBothDirections()
    {
        const string html = "<p>До картинки</p><figure><img src=\"https://assets.local/scheme.png\" alt=\"Схема\"><figcaption>Подпись</figcaption></figure>";
        var markdown = MarkdownService.FromHtml(html, "Документ");
        Assert.Contains("![Схема](https://assets.local/scheme.png \"Подпись\")", markdown);

        var restored = MarkdownService.ToHtml(markdown);
        Assert.Contains("src=\"https://assets.local/scheme.png\"", restored);
        Assert.Contains("До картинки", restored);
    }

    [Fact]
    public void ImageContentIsStoredInDatabaseAndRestoredToDisk()
    {
        var database = new DatabaseService();
        database.Initialize();
        var id = database.Create(null, false, "Страница с картинкой");
        database.StoreAssetContent("picture.png", "HASH-PNG", "image/png", Png);
        database.RegisterAttachment(id, "picture.png", "picture.png", "image/png", Png.Length, "HASH-PNG");
        database.SaveStructuredContent(id, "{\"type\":\"doc\"}", "<p><img src=\"https://assets.local/picture.png\" alt=\"Схема\"></p>", "Схема");

        Assert.NotNull(database.LoadAssetContent("picture.png"));
        Assert.Contains("picture.png", database.GetReferencedAssetNames(id));

        // Локальный файл — лишь кэш: после его удаления содержимое восстанавливается из базы.
        File.Delete(Path.Combine(AppPaths.Assets, "picture.png"));
        database.LoadStructuredContent(id);
        Assert.True(File.Exists(Path.Combine(AppPaths.Assets, "picture.png")));
    }

    [Fact]
    public void PrivatePageIsVisibleOnlyToItsOwner()
    {
        var database = new DatabaseService();
        database.Initialize();
        UserIdentity.Set("alice");
        var privateId = database.Create(null, false, "Приватная страница", true);
        var sharedId = database.Create(null, false, "Общая страница");
        Assert.Contains(database.LoadTree(), x => x.Id == privateId);
        Assert.True(database.GetDocumentAccess(privateId).IsPrivate);

        UserIdentity.Set("bob");
        Assert.DoesNotContain(database.LoadTree(), x => x.Id == privateId);
        Assert.Contains(database.LoadTree(), x => x.Id == sharedId);
        Assert.Null(database.FindNode(privateId));
        Assert.DoesNotContain(database.SearchDocuments("Приватная"), x => x.Id == privateId);

        UserIdentity.Set("alice");
        Assert.Contains(database.LoadTree(), x => x.Id == privateId);
    }

    [Fact]
    public void SelectedPagesAreCopiedToAnotherDatabaseWithImages()
    {
        var source = new SqliteDocumentStore(Path.Combine(AppPaths.Data, "source.db"));
        source.Initialize();
        var target = new SqliteDocumentStore(Path.Combine(AppPaths.Data, "target.db"));
        target.Initialize();

        var folder = source.Create(null, true, "Раздел");
        var wanted = source.Create(folder, false, "Нужная страница");
        source.Create(folder, false, "Лишняя страница");
        source.SaveStructuredContent(wanted, "{\"type\":\"doc\"}", "<p>Текст <img src=\"https://assets.local/scheme.png\" alt=\"Схема\"></p>", "Текст");
        source.SaveAssetContent("scheme.png", "HASH-SCHEME", "image/png", Png);

        StorageMigrationService.CopySelected(source, target, [wanted], true);

        var copied = target.LoadTree().SelectMany(Flatten).ToList();
        Assert.Contains(copied, x => x.Title == "Нужная страница");
        Assert.DoesNotContain(copied, x => x.Title == "Лишняя страница");
        Assert.Contains(copied, x => x.Title == "Раздел" && x.IsFolder);

        var document = copied.Single(x => x.Title == "Нужная страница");
        Assert.True(target.GetDocumentAccess(document.Id).IsPrivate);
        Assert.NotNull(target.LoadAssetContent("scheme.png"));
        Assert.Contains("Текст", target.LoadStructuredContent(document.Id).PlainText);
    }

    /// <summary>
    /// Содержимое страницы (JSON редактора, HTML и Markdown) должно доезжать целиком: именно из
    /// EditorJson редактор рисует страницу, и при его потере документ открывается пустым.
    /// Удалённое в корзину при этом не переносится.
    /// </summary>
    [Fact]
    public void PageContentSurvivesTransferAndTrashIsNotCopied()
    {
        var source = new SqliteDocumentStore(Path.Combine(AppPaths.Data, "content-source.db"));
        source.Initialize();
        var target = new SqliteDocumentStore(Path.Combine(AppPaths.Data, "content-target.db"));
        target.Initialize();

        var json = "{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"Текст страницы\"}]}]}";
        var page = source.Create(null, false, "Страница с содержимым");
        source.SaveStructuredContent(page, json, "<h1>Раздел</h1><p>Текст страницы</p>", "Раздел Текст страницы");
        var removed = source.Create(null, false, "Удалённая страница");
        source.SaveStructuredContent(removed, json, "<p>Удалённая</p>", "Удалённая");
        source.Delete(removed);

        StorageMigrationService.CopyAll(source, target);

        var copied = target.LoadTree().SelectMany(Flatten).ToList();
        Assert.DoesNotContain(copied, x => x.Title == "Удалённая страница");
        Assert.Empty(target.LoadTrash());

        var document = copied.Single(x => x.Title == "Страница с содержимым");
        var content = target.LoadStructuredContent(document.Id);
        Assert.Equal(json, content.Json);
        Assert.Contains("<h1>Раздел</h1>", content.Html);
        Assert.Contains("Текст страницы", content.PlainText);
        Assert.Contains("Текст страницы", target.GetDocumentMarkdown(document.Id));
    }

    /// <summary>Документ, помеченный шаблоном, попадает в список шаблонов и остаётся в дереве.</summary>
    [Fact]
    public void TemplateAppearsInTemplatesList()
    {
        var database = new DatabaseService();
        database.Initialize();

        var id = database.Create(null, false, "Шаблон отчёта");
        database.SetTemplate(id, true);

        Assert.Contains(database.LoadTemplates(), x => x.Id == id);
        // Дерево отдаёт признак шаблона — по нему список документов скрывает такие страницы.
        Assert.True(database.LoadTree().SelectMany(Flatten).Single(x => x.Id == id).IsTemplate);
    }

    /// <summary>Вложенность приходит из хранилища: документ в подпапке — ребёнок этой подпапки.</summary>
    [Fact]
    public void NestedDocumentStaysInsideItsSubfolder()
    {
        var database = new DatabaseService();
        database.Initialize();

        var section = database.Create(null, true, "Раздел");
        var subfolder = database.Create(section, true, "Подраздел");
        var page = database.Create(subfolder, false, "Страница подраздела");

        var root = Assert.Single(database.LoadTree());
        var child = Assert.Single(root.Children);
        Assert.Equal(subfolder, child.Id);
        Assert.Equal(page, Assert.Single(child.Children).Id);
    }

    /// <summary>
    /// Папка и документ — разные типы элементов: одинаковое название в одной папке допустимо,
    /// а вот два документа или две папки с одним названием — нет.
    /// </summary>
    [Fact]
    public void FolderAndDocumentMayShareTitleButSameKindMayNot()
    {
        var database = new DatabaseService();
        database.Initialize();

        var folder = database.Create(null, true, "Раздел");
        var document = database.Create(null, false, "Раздел");
        Assert.NotEqual(folder, document);

        var sameFolder = Assert.Throws<InvalidOperationException>(() => database.Create(null, true, "Раздел"));
        Assert.Contains("папка", sameFolder.Message);
        var sameDocument = Assert.Throws<InvalidOperationException>(() => database.Create(null, false, "Раздел"));
        Assert.Contains("документ", sameDocument.Message);

        Assert.True(database.TitleExists(null, "Раздел", null, true));
        Assert.True(database.TitleExists(null, "Раздел", null, false));
        Assert.False(database.TitleExists(null, "Раздел 2", null, false));
    }

    /// <summary>
    /// Повторная выгрузка тех же страниц обновляет их в целевой базе, а не создаёт копии —
    /// ни самих страниц, ни записей о вложениях.
    /// </summary>
    [Fact]
    public void RepeatedTransferUpdatesPagesInsteadOfDuplicatingThem()
    {
        var source = new SqliteDocumentStore(Path.Combine(AppPaths.Data, "repeat-source.db"));
        source.Initialize();
        var target = new SqliteDocumentStore(Path.Combine(AppPaths.Data, "repeat-target.db"));
        target.Initialize();

        var folder = source.Create(null, true, "Раздел");
        var page = source.Create(folder, false, "Страница");
        source.SaveStructuredContent(page, "{\"v\":1}", "<p>Первая редакция</p>", "Первая редакция");
        source.SaveAssetContent("scheme.png", "HASH-SCHEME", "image/png", Png);
        source.RegisterAttachment(page, "scheme.png", "scheme.png", "image/png", Png.LongLength, "HASH-SCHEME");

        StorageMigrationService.CopyAll(source, target);
        source.SaveStructuredContent(page, "{\"v\":2}", "<p>Вторая редакция</p>", "Вторая редакция");
        var report = StorageMigrationService.CopyAll(source, target);

        var copied = target.LoadTree().SelectMany(Flatten).ToList();
        Assert.Single(copied, x => x.Title == "Страница");
        Assert.Single(copied, x => x.Title == "Раздел");
        Assert.Equal(1, report.Updated);

        var document = copied.Single(x => x.Title == "Страница");
        Assert.Equal("{\"v\":2}", target.LoadStructuredContent(document.Id).Json);
        Assert.Single(target.GetAttachments(document.Id));
        Assert.Equal(source.GetDocumentGuid(page), target.GetDocumentGuid(document.Id));
    }

    [Fact]
    public void PdfExportContainsDocumentTextEvenWithoutPlainText()
    {
        var html = "<h1>Заголовок раздела</h1>" + string.Concat(Enumerable.Repeat("<p>Текст страницы документации для проверки экспорта в PDF.</p>", 40));
        var withText = Path.Combine(AppPaths.Temp, "with-text.pdf");
        var empty = Path.Combine(AppPaths.Temp, "empty.pdf");
        var exporter = new DocumentExportService(AppPaths.Assets);

        exporter.Export(html, "", "Документ", withText);
        exporter.Export("", "", "Документ", empty);

        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(withText).Take(4).ToArray()));
        Assert.True(new FileInfo(withText).Length > new FileInfo(empty).Length + 1000, "Текст документа не попал в PDF.");
    }

    [Fact]
    public void PdfExportDrawsImages()
    {
        File.WriteAllBytes(Path.Combine(AppPaths.Assets, "picture.png"), Png);
        var exporter = new DocumentExportService(AppPaths.Assets);
        var withImage = Path.Combine(AppPaths.Temp, "image.pdf");
        var withoutImage = Path.Combine(AppPaths.Temp, "no-image.pdf");

        exporter.Export("<p>Текст</p><img src=\"https://assets.local/picture.png\" alt=\"Схема\">", "Текст", "Документ", withImage);
        exporter.Export("<p>Текст</p>", "Текст", "Документ", withoutImage);

        Assert.True(new FileInfo(withImage).Length > new FileInfo(withoutImage).Length, "Изображение не добавлено в PDF.");
    }

    [Fact]
    public void HtmlExportEmbedsImagesAndMarkdownExportCopiesThem()
    {
        File.WriteAllBytes(Path.Combine(AppPaths.Assets, "picture.png"), Png);
        const string html = "<p>Текст</p><img src=\"https://assets.local/picture.png\" alt=\"Схема\">";
        var exporter = new DocumentExportService(AppPaths.Assets);
        var htmlPath = Path.Combine(AppPaths.Temp, "page.html");
        var markdownPath = Path.Combine(AppPaths.Temp, "page.md");

        exporter.Export(html, "Текст", "Документ", htmlPath);
        exporter.Export(html, "Текст", "Документ", markdownPath);

        var exportedHtml = File.ReadAllText(htmlPath);
        Assert.Contains("data:image/png;base64,", exportedHtml);
        Assert.DoesNotContain("assets.local", exportedHtml);

        var exportedMarkdown = File.ReadAllText(markdownPath);
        Assert.Contains("page_files/picture.png", exportedMarkdown);
        Assert.True(File.Exists(Path.Combine(AppPaths.Temp, "page_files", "picture.png")));
    }

    [Fact]
    public void DocxExportEmbedsImages()
    {
        File.WriteAllBytes(Path.Combine(AppPaths.Assets, "picture.png"), Png);
        var path = Path.Combine(AppPaths.Temp, "with-image.docx");
        new DocumentExportService(AppPaths.Assets).Export("<p>Текст</p><img src=\"https://assets.local/picture.png\" alt=\"Схема\">", "Текст", "Документ", path);

        using var archive = System.IO.Compression.ZipFile.OpenRead(path);
        Assert.Contains(archive.Entries, entry => entry.FullName.Contains("media/", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<NodeItem> Flatten(NodeItem node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var item in Flatten(child))
                yield return item;
    }

    public void Dispose()
    {
        try { UserIdentity.Set(_originalUser); } catch { }
        try { if (Directory.Exists(AppPaths.Data)) Directory.Delete(AppPaths.Data, true); } catch { }
    }
}

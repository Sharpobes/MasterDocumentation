using System.IO.Compression;
using MasterDocumentation.Models;
using MasterDocumentation.Services;
using MasterDocumentation.Storage;
using MasterDocumentation.Utilities;
using System.Windows.Documents;
using System.Text.Json;

namespace MasterDocumentation.Tests;

public sealed class SettingsAndBackupTests : IDisposable
{
    public SettingsAndBackupTests() { if (Directory.Exists(AppPaths.Data)) Directory.Delete(AppPaths.Data, true); AppPaths.Ensure(); }
    [Fact]
    public void Settings_AreSavedAtomically_AndLoaded()
    {
        var service = new SettingsService(); var settings = new ApplicationSettings { Language = "English", RecentFilesCount = 27, AutoSaveDelaySeconds = 7 };
        service.Save(settings); var loaded = service.Load();
        Assert.Equal("English", loaded.Language); Assert.Equal(27, loaded.RecentFilesCount); Assert.Equal(7, loaded.AutoSaveDelaySeconds); Assert.False(File.Exists(AppPaths.Settings + ".tmp"));
    }
    [Fact]
    public void LegacySettings_AreMigratedWithoutShowingWizardToExistingStorage()
    {
        File.WriteAllText(AppPaths.Database, "existing-storage");
        File.WriteAllText(AppPaths.Settings, "{\"Version\":1,\"Language\":\"Русский\",\"Theme\":\"Системная\",\"RecentFilesCount\":999,\"AutoSaveDelaySeconds\":0,\"AutomaticBackupLimit\":0}");
        var loaded = new SettingsService().Load();
        Assert.Equal(2, loaded.Version); Assert.True(loaded.FirstRunCompleted); Assert.Equal(100, loaded.RecentFilesCount); Assert.Equal(1, loaded.AutoSaveDelaySeconds); Assert.Equal(1, loaded.AutomaticBackupLimit);
    }
    [Fact]
    public void NewDatabase_StartsEmptyWithoutDemonstrationDocuments()
    {
        var database = new DatabaseService(); database.Initialize();
        Assert.Equal(0, database.CountDocuments()); Assert.Empty(database.LoadTree());
    }
    [Fact]
    public void Backup_ContainsManifestDatabaseSettingsAndChecksums()
    {
        var database = new DatabaseService(); database.Initialize(); var service = new BackupService(database); var path = service.CreateBackup(false);
        Assert.EndsWith(".mdbackup", path); using var archive = ZipFile.OpenRead(path);
        Assert.NotNull(archive.GetEntry("manifest.json")); Assert.NotNull(archive.GetEntry("masterdocumentation.db")); Assert.NotNull(archive.GetEntry("settings-export.json")); Assert.NotNull(archive.GetEntry("checksum.json"));
    }
    [Fact]
    public void Search_FindsDocumentByTitle()
    {
        var database = new DatabaseService(); database.Initialize(); var id = database.Create(null, false, "Уникальная инструкция");
        Assert.Contains(database.LoadTree("Уникальная"), x => x.Id == id);
    }
    [Fact]
    public void DeletedDocument_IsMovedToTrash_AndCanBeRestored()
    {
        var database = new DatabaseService(); database.Initialize(); var id = database.Create(null, false, "Документ для корзины");
        database.Delete(id); Assert.DoesNotContain(database.LoadTree(), x => x.Id == id); Assert.Contains(database.LoadTrash(), x => x.Id == id);
        database.Restore(id); Assert.Contains(database.LoadTree(), x => x.Id == id); Assert.DoesNotContain(database.LoadTrash(), x => x.Id == id);
    }
    [Fact]
    public void FavoriteCount_ReflectsStoredState()
    {
        var database = new DatabaseService(); database.Initialize(); var id = database.Create(null, false, "Избранное");
        var before = database.CountFavorites(); database.ToggleFavorite(id); Assert.Equal(before + 1, database.CountFavorites());
    }
    [Fact]
    public void MarkdownExport_PreservesHeadingAndText()
    {
        var doc=new FlowDocument(new Paragraph(new Run("Раздел")){FontSize=24}); doc.Blocks.Add(new Paragraph(new Run("Содержимое")));
        var markdown=DocumentExportService.Markdown(doc,"Инструкция"); Assert.Contains("# Инструкция",markdown); Assert.Contains("# Раздел",markdown); Assert.Contains("Содержимое",markdown);
    }
    [Fact]
    public void DocxExport_CreatesValidPackage()
    {
        var doc=new FlowDocument(new Paragraph(new Run("Проверка DOCX"))); var path=Path.Combine(AppPaths.Temp,"test.docx"); new DocumentExportService().Export(doc,"Документ",path);
        Assert.True(File.Exists(path)); using var archive=ZipFile.OpenRead(path); Assert.NotNull(archive.GetEntry("word/document.xml"));
    }
    [Fact]
    public void SavingChangedContent_CreatesRestorableVersion()
    {
        var database=new DatabaseService();database.Initialize();var id=database.Create(null,false,"Версии");var first=new FlowDocument(new Paragraph(new Run("Первая")));database.SaveDocument(id,first,"Первая");var second=new FlowDocument(new Paragraph(new Run("Вторая")));database.SaveDocument(id,second,"Вторая");var version=Assert.Single(database.GetVersions(id));database.RestoreVersion(version.Id);var restored=database.LoadDocument(id).Document;Assert.Contains("Первая",new TextRange(restored.ContentStart,restored.ContentEnd).Text);
    }
    [Fact]
    public void AttachmentMetadata_IsStored()
    {
        var database=new DatabaseService();database.Initialize();var id=database.Create(null,false,"Вложения");database.RegisterAttachment(id,"image.png","abc.png","image/png",42,"HASH");var item=Assert.Single(database.GetAttachments(id));Assert.Equal("image.png",item.FileName);Assert.Equal(42,item.Size);
    }
    [Fact]
    public void EncryptedBackup_RequiresCorrectPassword()
    {
        var database=new DatabaseService();database.Initialize();var service=new BackupService(database);var path=service.CreateBackup(false,"correct-password");Assert.True(BackupService.IsEncrypted(path));Assert.Throws<UnauthorizedAccessException>(()=>service.Restore(path,"wrong-password"));service.Restore(path,"correct-password");Assert.Equal("ok",database.CheckIntegrity());
    }
    [Fact]
    public void PdfExport_CreatesPdfWithCyrillicContent()
    {
        var doc=new FlowDocument(new Paragraph(new Run("Проверка экспорта документа")));var path=Path.Combine(AppPaths.Temp,"test.pdf");new DocumentExportService().Export(doc,"Русский документ",path);var header=File.ReadAllBytes(path).Take(4).ToArray();Assert.Equal("%PDF",System.Text.Encoding.ASCII.GetString(header));Assert.True(new FileInfo(path).Length>500);
    }
    [Fact]
    public void StructuredEditorContent_IsPrimaryAndVersioned()
    {
        var database=new DatabaseService();database.Initialize();var id=database.Create(null,false,"TipTap");database.SaveStructuredContent(id,"{\"type\":\"doc\",\"content\":[]}","<p>Первая</p>","Первая");database.SaveStructuredContent(id,"{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\"}]}","<p>Вторая</p>","Вторая");var content=database.LoadStructuredContent(id);Assert.Equal("Вторая",content.PlainText);Assert.Single(database.GetVersions(id));
    }
    [Fact]
    public void DuplicateTitle_InSameFolder_IsRejectedCaseInsensitively()
    {
        var database=new DatabaseService();database.Initialize();var folder=database.Create(null,true,"Папка");database.Create(folder,false,"Test");Assert.Throws<InvalidOperationException>(()=>database.Create(folder,false,"test"));
    }
    [Fact]
    public void Template_CreatesIndependentStructuredDocument()
    {
        var database=new DatabaseService();database.Initialize();var source=database.Create(null,false,"Шаблон отчёта");database.SaveStructuredContent(source,"{\"type\":\"doc\"}","<h1>Отчёт</h1>","Отчёт");database.SetTemplate(source,true);var created=database.CreateFromTemplate(source,null,"Отчёт за июль");var content=database.LoadStructuredContent(created);Assert.Equal("Отчёт",content.PlainText);Assert.Equal("<h1>Отчёт</h1>",content.Html);
    }
    [Fact]
    public void TemplateVariables_AreSubstitutedAndHtmlEncoded()
    {
        var database=new DatabaseService();database.Initialize();var source=database.Create(null,false,"Шаблон с переменными");database.SaveStructuredContent(source,"{\"type\":\"doc\",\"title\":\"{{Title}}\",\"client\":\"{{Клиент}}\"}","<h1>{{Title}}</h1><p>{{Клиент}}</p>","{{Title}} — {{Клиент}}");database.SetTemplate(source,true);
        Assert.Contains("Клиент",database.GetTemplateVariables(source));var created=database.CreateFromTemplate(source,null,"Отчёт",new Dictionary<string,string>{{"Клиент","ООО <Тест>"}});var content=database.LoadStructuredContent(created);Assert.Contains("ООО <Тест>",content.PlainText);Assert.Contains("ООО &lt;Тест&gt;",content.Html);using var json=JsonDocument.Parse(content.Json);Assert.Equal("Отчёт",json.RootElement.GetProperty("title").GetString());
    }
    [Fact]
    public void InternalDocumentGuid_ResolvesToTheStoredDocument()
    {
        var database=new DatabaseService();database.Initialize();var id=database.Create(null,false,"Связанный документ");var guid=database.GetDocumentGuid(id);Assert.True(Guid.TryParse(guid,out _));Assert.Equal(id,database.FindDocumentByGuid(guid)?.Id);
    }
    [Fact]
    public void Move_RejectsCyclesAndMovesDocumentToFolder()
    {
        var database=new DatabaseService();database.Initialize();var parent=database.Create(null,true,"Раздел");var child=database.Create(parent,true,"Подраздел");var document=database.Create(null,false,"Инструкция");database.Move(document,child);Assert.Equal(child,database.FindNode(document)?.ParentId);Assert.Throws<InvalidOperationException>(()=>database.Move(parent,child));
    }
    [Fact]
    public void StructuredVersion_RestoresJsonAndHtml()
    {
        var database=new DatabaseService();database.Initialize();var id=database.Create(null,false,"Структурные версии");var first="{\"type\":\"doc\",\"content\":[{\"type\":\"heading\"}]}";database.SaveStructuredContent(id,first,"<h1>Первая</h1>","Первая");database.SaveStructuredContent(id,"{\"type\":\"doc\",\"content\":[]}","<p>Вторая</p>","Вторая");var version=Assert.Single(database.GetVersions(id));database.RestoreVersion(version.Id);var restored=database.LoadStructuredContent(id);Assert.Equal(first,restored.Json);Assert.Equal("<h1>Первая</h1>",restored.Html);Assert.Equal("Первая",restored.PlainText);
    }
    [Fact]
    public void Duplicate_CopiesTagsAttachmentsAndStructuredContent()
    {
        var database=new DatabaseService();database.Initialize();var id=database.Create(null,false,"Полный документ");database.SaveStructuredContent(id,"{\"type\":\"doc\"}","<p>Текст</p>","Текст");database.SetTags(id,["система","обзор"]);database.RegisterAttachment(id,"scheme.pdf","hash.pdf","application/pdf",123,"HASH-PDF");var copy=database.Duplicate(id);Assert.Equal("Текст",database.LoadStructuredContent(copy).PlainText);Assert.Equal(2,database.GetTags(copy).Count);Assert.Equal("scheme.pdf",Assert.Single(database.GetAttachments(copy)).FileName);
    }
    [Fact]
    public void SearchIndex_IncludesTagsAndAttachmentNames()
    {
        var database=new DatabaseService();database.Initialize();var id=database.Create(null,false,"Нейтральное название");database.SetTags(id,["уникальныйтег"]);database.RegisterAttachment(id,"особаясхема.pdf","file.pdf","application/pdf",10,"SEARCH-HASH");Assert.Contains(database.LoadTree("уникальныйтег"),x=>x.Id==id);Assert.Contains(database.LoadTree("особаясхема"),x=>x.Id==id);
    }
    [Fact]
    public void GlobalSearch_FiltersFtsResultsByStatusTagsAndAttachments()
    {
        var database=new DatabaseService();database.Initialize();var id=database.Create(null,false,"Архитектура решения");database.SaveStructuredContent(id,"{\"type\":\"doc\"}","<p>SQLite хранилище</p>","SQLite хранилище");database.SetStatus(id,"В работе");database.SetTags(id,["система"]);database.RegisterAttachment(id,"scheme.png","scheme.png","image/png",12,"SCHEME");
        var found=Assert.Single(database.SearchDocuments("SQLite","В работе","система",attachmentsOnly:true));Assert.Equal(id,found.Id);Assert.Contains("⟦",found.Snippet);Assert.Empty(database.SearchDocuments("SQLite","Архив"));
    }
    [Fact]
    public void CustomStatusesAndProperties_AreValidatedAndPersisted()
    {
        var database=new DatabaseService();database.Initialize();var id=database.Create(null,false,"Карточка документа");database.AddStatus("Согласование");database.SetStatus(id,"Согласование");database.SetCustomProperties(id,[new("Заказчик","ООО Тест"),new("Версия","2.0")]);
        Assert.Contains("Согласование",database.GetStatuses());Assert.Equal("Согласование",database.GetStatus(id));Assert.Equal(2,database.GetCustomProperties(id).Count);Assert.Equal("ООО Тест",database.GetCustomProperties(id).Single(x=>x.Name=="Заказчик").Value);Assert.Throws<InvalidOperationException>(()=>database.DeleteStatus("Согласование"));
    }
    [Fact]
    public void RichHtmlExport_PreservesFormattingInHtmlAndDocx()
    {
        const string html="<h1>Заголовок</h1><p><strong>Важный</strong> текст</p><table><tbody><tr><th>Колонка</th></tr><tr><td>Значение</td></tr></tbody></table>";var exporter=new DocumentExportService();var htmlPath=Path.Combine(AppPaths.Temp,"rich.html");var docxPath=Path.Combine(AppPaths.Temp,"rich.docx");exporter.Export(html,"Заголовок Важный текст Колонка Значение","Документ",htmlPath);exporter.Export(html,"Заголовок Важный текст Колонка Значение","Документ",docxPath);Assert.Contains("<strong>Важный</strong>",File.ReadAllText(htmlPath));using var archive=ZipFile.OpenRead(docxPath);using var reader=new StreamReader(archive.GetEntry("word/document.xml")!.Open());var xml=reader.ReadToEnd();Assert.Contains("Heading1",xml);Assert.Contains("Важный",xml);Assert.Contains("Колонка",xml);
    }
    [Fact]
    public void EmergencyDraft_IsWrittenAtomicallyAndCanBeRemoved()
    {
        var tab=new DocumentTab{DocumentId=77,Title="Черновик",EditorJson="{\"type\":\"doc\"}",Html="<p>Не потерять</p>",PlainText="Не потерять",IsDirty=true};DraftRecoveryService.Write(tab);var draft=Assert.Single(DraftRecoveryService.ReadAll());Assert.Equal(77,draft.DocumentId);Assert.Equal("Не потерять",draft.PlainText);Assert.False(File.Exists(Path.Combine(AppPaths.Temp,"Drafts","77.json.tmp")));DraftRecoveryService.Delete(77);Assert.Empty(DraftRecoveryService.ReadAll());
    }
    [Fact]
    public void DocumentVersion_CanBePinnedAndUnpinned()
    {
        var database=new DatabaseService();database.Initialize();var id=database.Create(null,false,"Закреплённая версия");database.SaveStructuredContent(id,"{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\"}]}","<p>Первая</p>","Первая");database.SaveStructuredContent(id,"{\"type\":\"doc\",\"content\":[{\"type\":\"heading\"}]}","<h1>Вторая</h1>","Вторая");var version=Assert.Single(database.GetVersions(id));Assert.False(version.IsPinned);database.ToggleVersionPin(version.Id);Assert.True(Assert.Single(database.GetVersions(id)).IsPinned);database.ToggleVersionPin(version.Id);Assert.False(Assert.Single(database.GetVersions(id)).IsPinned);
    }
    [Fact]
    public void MarkdownExport_PreservesAdvancedDocumentBlocks()
    {
        const string html="<div data-label='Важно' data-callout='true' data-kind='warning'><p>Проверьте данные</p></div><p>Формула: <span data-formula='true'>E = mc^2</span></p><div data-mermaid='true'>flowchart LR; A--&gt;B</div><span data-document-anchor='схема'>⚓ схема</span><div data-safe-html='true'>&lt;section&gt;Локально&lt;/section&gt;</div><div data-page-break='true'></div>";var markdown=DocumentExportService.MarkdownFromHtml(html,"Схема");Assert.Contains("> **Важно**",markdown);Assert.Contains("$E = mc^2$",markdown);Assert.Contains("```mermaid",markdown);Assert.Contains("flowchart LR; A-->B",markdown);Assert.Contains("<a id=\"схема\"></a>",markdown);Assert.Contains("```html",markdown);Assert.Contains("<section>Локально</section>",markdown);Assert.Contains("page-break-after",markdown);
    }
    public void Dispose() { try { if (Directory.Exists(AppPaths.Data)) Directory.Delete(AppPaths.Data, true); } catch { } }
}

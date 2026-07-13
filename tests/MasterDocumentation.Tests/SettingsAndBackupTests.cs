using System.IO.Compression;
using MasterDocumentation.Models;
using MasterDocumentation.Services;
using MasterDocumentation.Storage;
using MasterDocumentation.Utilities;
using System.Windows.Documents;

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
    public void Dispose() { try { if (Directory.Exists(AppPaths.Data)) Directory.Delete(AppPaths.Data, true); } catch { } }
}

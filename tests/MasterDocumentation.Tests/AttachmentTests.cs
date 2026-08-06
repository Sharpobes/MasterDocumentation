using MasterDocumentation.Services;
using MasterDocumentation.Storage;
using MasterDocumentation.Utilities;

namespace MasterDocumentation.Tests;

/// <summary>
/// Вложения: привязка к конкретному документу и удаление файла вместе со связью.
/// </summary>
public sealed class AttachmentTests : IDisposable
{
    private readonly DatabaseService _database;

    public AttachmentTests()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(AppPaths.Data)) Directory.Delete(AppPaths.Data, true);
        AppPaths.Ensure();
        _database = new DatabaseService();
        _database.Initialize();
    }

    public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    /// <summary>Файл виден только у того документа, к которому его добавили.</summary>
    [Fact]
    public void RegisterAttachment_BindsFileToSingleDocument()
    {
        var first = _database.Create(null, false, "Первый");
        var second = _database.Create(null, false, "Второй");

        _database.RegisterAttachment(first, "схема.png", "abc.png", "image/png", 12, "ABC");

        Assert.Single(_database.GetAttachments(first));
        Assert.Empty(_database.GetAttachments(second));
    }

    /// <summary>
    /// Один и тот же файл у двух документов хранится один раз; пока на него ссылается второй
    /// документ, файл остаётся, и удаляется только когда исчезает последняя ссылка.
    /// </summary>
    [Fact]
    public void RemoveAttachment_KeepsFileWhileAnotherDocumentUsesIt()
    {
        var first = _database.Create(null, false, "Первый");
        var second = _database.Create(null, false, "Второй");
        var one = _database.RegisterAttachment(first, "схема.png", "abc.png", "image/png", 12, "ABC");
        var two = _database.RegisterAttachment(second, "схема.png", "abc.png", "image/png", 12, "ABC");

        Assert.False(_database.RemoveAttachment(one, ignoreVersions: true).IsUnused);
        Assert.True(_database.RemoveAttachment(two, ignoreVersions: true).IsUnused);
    }

    /// <summary>
    /// Пока картинка стоит в тексте, файл считается используемым; после её удаления из содержимого
    /// связь снимается вместе с файлом — иначе вложение исчезало только из списка.
    /// </summary>
    [Fact]
    public void RemoveAttachment_ReportsFileUnusedOnlyAfterImageLeavesContent()
    {
        var id = _database.Create(null, false, "Документ");
        var attachment = _database.RegisterAttachment(id, "схема.png", "abc.png", "image/png", 12, "ABC");
        _database.SaveStructuredContent(id, "", "<p><img src=\"https://assets.local/abc.png\"></p>", "");

        Assert.False(_database.RemoveAttachment(attachment, ignoreVersions: true).IsUnused);

        var again = _database.RegisterAttachment(id, "схема.png", "abc.png", "image/png", 12, "ABC");
        _database.SaveStructuredContent(id, "", "<p>Без картинки</p>", "Без картинки");

        Assert.True(_database.RemoveAttachment(again, ignoreVersions: true).IsUnused);
    }
}

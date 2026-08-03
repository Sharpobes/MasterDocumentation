using MasterDocumentation.Services;
using MasterDocumentation.Storage;
using MasterDocumentation.Utilities;

namespace MasterDocumentation.Tests;

/// <summary>
/// Корзина: перемещение туда и обратно, окончательное удаление и автоматическая очистка
/// по сроку хранения.
/// </summary>
public sealed class TrashTests : IDisposable
{
    private readonly DatabaseService _database;

    public TrashTests()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(AppPaths.Data)) Directory.Delete(AppPaths.Data, true);
        AppPaths.Ensure();
        _database = new DatabaseService();
        _database.Initialize();
    }

    public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    [Fact]
    public void Delete_MovesDocumentToTrash()
    {
        var id = _database.Create(null, false, "Документ");

        _database.Delete(id);

        Assert.Equal(0, _database.CountDocuments());
        Assert.Equal(1, _database.CountTrash());
        Assert.Contains(_database.LoadTrash(), node => node.Id == id);
    }

    /// <summary>Вложенный документ попадает в корзину вместе с папкой и виден в ней.</summary>
    [Fact]
    public void Delete_MovesWholeBranchToTrash()
    {
        var folder = _database.Create(null, true, "Папка");
        var document = _database.Create(folder, false, "Внутри");

        _database.Delete(folder);

        var trash = _database.LoadTrash();
        Assert.Contains(trash, node => node.Id == folder);
        Assert.Contains(trash, node => node.Id == document);
        Assert.Empty(_database.LoadTree());
    }

    [Fact]
    public void Restore_ReturnsDocumentFromTrash()
    {
        var id = _database.Create(null, false, "Документ");
        _database.Delete(id);

        _database.Restore(id);

        Assert.Equal(0, _database.CountTrash());
        Assert.Equal(1, _database.CountDocuments());
    }

    [Fact]
    public void DeletePermanently_RemovesDocumentFromTrash()
    {
        var id = _database.Create(null, false, "Документ");
        _database.Delete(id);

        _database.DeletePermanently(id);

        Assert.Equal(0, _database.CountTrash());
        Assert.Equal(0, _database.CountDocuments());
    }

    /// <summary>
    /// Документ вне корзины окончательное удаление не трогает — команда доступна только в корзине,
    /// иначе документ молча оставался бы на месте.
    /// </summary>
    [Fact]
    public void DeletePermanently_IgnoresDocumentOutsideTrash()
    {
        var id = _database.Create(null, false, "Документ");

        _database.DeletePermanently(id);

        Assert.Equal(1, _database.CountDocuments());
    }

    [Fact]
    public void PurgeExpiredTrash_RemovesOnlyExpiredItems()
    {
        var fresh = _database.Create(null, false, "Свежий");
        var expired = _database.Create(null, false, "Просроченный");
        _database.Delete(fresh);
        _database.Delete(expired);
        SetDeletedAt(expired, DateTime.UtcNow.AddDays(-DatabaseService.TrashRetentionDays - 1));

        var removed = _database.PurgeExpiredTrash();

        Assert.Equal(1, removed);
        Assert.Equal(1, _database.CountTrash());
        Assert.Contains(_database.LoadTrash(), node => node.Id == fresh);
    }

    [Fact]
    public void PurgeExpiredTrash_KeepsEverythingWhenRetentionIsDisabled()
    {
        var id = _database.Create(null, false, "Документ");
        _database.Delete(id);
        SetDeletedAt(id, DateTime.UtcNow.AddYears(-5));

        Assert.Equal(0, _database.PurgeExpiredTrash(0));
        Assert.Equal(1, _database.CountTrash());
    }

    /// <summary>Состарить запись в корзине можно только напрямую в базе: API даты удаления не меняет.</summary>
    private static void SetDeletedAt(long id, DateTime moment)
    {
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={AppPaths.Database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Nodes SET DeletedAt=$d WHERE Id=$id";
            command.Parameters.AddWithValue("$d", moment.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}

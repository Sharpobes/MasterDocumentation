namespace MasterDocumentation.Models;

/// <summary>
/// Права доступа к странице документации в общей базе данных.
/// Приватная страница видна только своему владельцу; обычная — всем, кто подключился
/// к этой базе через приложение.
/// </summary>
public sealed record DocumentAccess(long DocumentId, bool IsPrivate, string Owner)
{
    public bool IsOwnedBy(string user) => string.Equals(Owner, user, StringComparison.OrdinalIgnoreCase);
}

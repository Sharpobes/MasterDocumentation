using MasterDocumentation.Utilities;

namespace MasterDocumentation.Storage;

/// <summary>
/// Пояснение к конфликту названий. Название проверяется только среди элементов своего типа:
/// папка и документ с одинаковым названием в одной папке — это разные элементы, они друг другу
/// не мешают. Раньше при отказе показывалось общее «уже существует элемент», из-за которого было
/// непонятно, что именно мешает: одноимённый элемент того же типа или вовсе невидимая приватная
/// страница другого пользователя.
/// </summary>
internal static class TitleConflicts
{
    /// <summary>Описание занятого названия для сообщения пользователю.</summary>
    public static string Describe(string existingTitle, bool isFolder, bool isPrivate, string owner, string where = "В этой папке")
    {
        if (isPrivate && owner.Length > 0 && !string.Equals(owner, UserIdentity.Current, StringComparison.OrdinalIgnoreCase))
            return $"Название «{existingTitle}» занято приватной страницей другого пользователя ({owner}). Она находится в этой папке, но не видна в дереве. Выберите другое название.";
        var kind = isFolder ? "папка" : "документ";
        return $"{where} уже есть {kind} «{existingTitle}». Выберите другое название.";
    }

    /// <summary>Сообщение на случай, когда подробности конфликта прочитать не удалось.</summary>
    public static string Generic(string title, string where = "В этой папке")
        => $"{where} уже есть элемент с названием «{title}». Выберите другое название.";
}

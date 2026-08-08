using Npgsql;

namespace MasterDocumentation.Storage;

/// <summary>
/// Проверка строки подключения до попытки соединения. Npgsql на опечатку отвечает
/// сообщением вида «Couldn't set host-localhost;port», по которому непонятно, что именно
/// исправить, поэтому строка разбирается самостоятельно и пользователю называется конкретный
/// сбойный фрагмент.
/// </summary>
public static class PostgresConnectionString
{
    public const string Example = "Host=localhost;Port=5432;Database=masterdocumentation;Username=postgres;Password=***";

    /// <summary>Проверяет строку и объясняет, что именно в ней не так.</summary>
    public static bool TryValidate(string? value, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Строка подключения не заполнена. Ожидается: " + Example;
            return false;
        }

        // Разбор по парам «ключ=значение»: так видно фрагмент с опечаткой, а не всю строку целиком.
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains('=')) continue;
            var dash = part.Contains('-') ? " — похоже, вместо «=» набран дефис" : "";
            error = $"Фрагмент «{part}» записан без знака «=»{dash}. Каждый параметр указывается как Ключ=Значение и отделяется точкой с запятой. Ожидается: {Example}";
            return false;
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(value);
        }
        catch (Exception ex)
        {
            error = $"Строку подключения не удалось разобрать: {ex.Message}. Ожидается: {Example}";
            return false;
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(builder.Host)) missing.Add("Host (адрес сервера)");
        if (string.IsNullOrWhiteSpace(builder.Database)) missing.Add("Database (имя базы)");
        if (string.IsNullOrWhiteSpace(builder.Username)) missing.Add("Username (пользователь базы)");
        if (missing.Count > 0)
        {
            error = $"В строке подключения не хватает обязательных параметров: {string.Join(", ", missing)}. Ожидается: {Example}";
            return false;
        }
        return true;
    }

    /// <summary>То же самое, но с исключением — для точек, где продолжать работу нельзя.</summary>
    public static void Validate(string? value)
    {
        if (!TryValidate(value, out var error)) throw new InvalidOperationException(error);
    }
}

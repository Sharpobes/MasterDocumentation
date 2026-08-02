using System.IO;

namespace MasterDocumentation.Utilities;

/// <summary>
/// Имя пользователя, от которого приложение работает с общей базой данных. Используется как
/// владелец приватных страниц документации: их видит только тот, кто их создал, все остальные
/// подключившиеся к той же базе видят лишь общие страницы.
/// Имя фиксируется в файле рядом с настройками хранилища, чтобы не меняться при переименовании
/// учётной записи Windows и переносе данных.
/// </summary>
public static class UserIdentity
{
    private static string? _current;
    private static string ConfigPath => Path.Combine(AppPaths.Root, "storage-user.txt");

    public static string Current => _current ??= Load();

    public static string DisplayName => Current;

    public static void Set(string value)
    {
        var name = Normalize(value);
        var temporary = ConfigPath + ".tmp";
        File.WriteAllText(temporary, name);
        File.Move(temporary, ConfigPath, true);
        _current = name;
    }

    private static string Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var stored = Normalize(File.ReadAllText(ConfigPath));
                if (stored.Length > 0) return stored;
            }
        }
        catch { /* имя пользователя не критично — ниже подставляется учётная запись Windows */ }
        var fallback = Normalize($"{Environment.UserName}@{Environment.MachineName}");
        try { Set(fallback); } catch { /* запись имени не обязательна для работы */ }
        return fallback;
    }

    private static string Normalize(string value)
    {
        var name = (value ?? "").Trim();
        if (name.Length == 0) name = "Локальный пользователь";
        return name.Length > 120 ? name[..120] : name;
    }
}

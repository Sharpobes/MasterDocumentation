using Npgsql;

namespace MasterDocumentation.Storage;

/// <summary>
/// Человекочитаемое описание ошибок работы с PostgreSQL. Сообщения Npgsql и сервера приходят
/// технические (а часть — по-английски), поэтому в интерфейсе они дополняются пояснением
/// «что именно не так» и подсказкой «что сделать». Технические подробности (код SqlState,
/// таблица, деталь сервера) сохраняются — они нужны при обращении к администратору БД.
/// </summary>
public static class PostgresErrorInfo
{
    /// <summary>Короткая строка для полосы состояния.</summary>
    public static string Short(Exception exception)
    {
        var error = Unwrap(exception);
        return Explain(error).Reason;
    }

    /// <summary>Развёрнутое описание: причина, подсказка и технические подробности.</summary>
    public static string Detailed(Exception exception)
    {
        var error = Unwrap(exception);
        var (reason, hint) = Explain(error);
        var lines = new List<string> { reason };
        if (hint.Length > 0) { lines.Add(""); lines.Add("Что сделать: " + hint); }
        lines.Add("");
        lines.Add("Технические подробности:");
        lines.Add(error.GetType().Name + ": " + error.Message);
        if (error is PostgresException postgres)
        {
            lines.Add($"Код ошибки сервера: {postgres.SqlState}");
            if (!string.IsNullOrWhiteSpace(postgres.Detail)) lines.Add("Деталь: " + postgres.Detail);
            if (!string.IsNullOrWhiteSpace(postgres.Hint)) lines.Add("Подсказка сервера: " + postgres.Hint);
            if (!string.IsNullOrWhiteSpace(postgres.TableName)) lines.Add("Таблица: " + postgres.TableName);
            if (!string.IsNullOrWhiteSpace(postgres.ColumnName)) lines.Add("Колонка: " + postgres.ColumnName);
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static (string Reason, string Hint) Explain(Exception error) => error switch
    {
        PostgresException postgres => postgres.SqlState switch
        {
            "28P01" or "28000" => ("Сервер PostgreSQL отклонил учётные данные.", "Проверьте Username и Password в строке подключения и правила доступа в pg_hba.conf."),
            "3D000" => ("Указанной базы данных нет на сервере.", "Приложение создаёт базу само — для этого пользователю нужно право CREATEDB, либо создайте базу вручную."),
            "42501" => ("У пользователя недостаточно прав на эту операцию в базе.", "Выдайте пользователю права на базу (GRANT ALL ON DATABASE … / схему public) или подключитесь владельцем базы."),
            "42P01" => ("В базе нет нужной таблицы — схема ещё не создана или создана не полностью.", "Повторите перенос: приложение создаёт все таблицы само при подключении к базе."),
            "42883" => ("Сервер не знает функцию, которую использует приложение.", "Проверьте версию PostgreSQL — требуется 12 или новее."),
            "53300" => ("На сервере исчерпан лимит подключений.", "Закройте лишние подключения или увеличьте max_connections."),
            "57P03" => ("Сервер PostgreSQL сейчас не принимает подключения (запускается или восстанавливается).", "Дождитесь запуска сервера и повторите."),
            _ => ("Сервер PostgreSQL отклонил запрос: " + postgres.MessageText, ""),
        },
        InvalidCastException cast when cast.Message.Contains("<unknown>", StringComparison.Ordinal)
            => ("Клиент не смог прочитать тип данных колонки: список типов базы устарел (обычно так бывает, если расширение citext было установлено уже после подключения).",
                "Закройте окно и повторите операцию — приложение перечитывает каталог типов при инициализации базы. Если повторяется, перезапустите приложение."),
        InvalidCastException cast => ("Несовместимый тип данных при чтении из базы: " + cast.Message, "Возможно, база создана другой версией приложения — перенесите данные в чистую базу."),
        TimeoutException => ("Истекло время ожидания ответа от сервера PostgreSQL.", "Проверьте доступность сервера и сети; при переносе больших объёмов увеличьте Timeout в строке подключения."),
        NpgsqlException npgsql => ("Не удалось связаться с сервером PostgreSQL: " + npgsql.Message, "Проверьте Host и Port, что служба PostgreSQL запущена и порт не закрыт брандмауэром."),
        ArgumentException or FormatException => ("Строка подключения заполнена неверно: " + error.Message, "Каждый параметр указывается как Ключ=Значение через точку с запятой. Ожидается: " + PostgresConnectionString.Example),
        _ => (error.Message, ""),
    };

    private static Exception Unwrap(Exception exception)
    {
        var current = exception;
        while (current is AggregateException aggregate && aggregate.InnerExceptions.Count == 1) current = aggregate.InnerExceptions[0];
        if (current is PostgresException or NpgsqlException) return current;
        return current.InnerException is PostgresException or NpgsqlException ? current.InnerException : current;
    }
}

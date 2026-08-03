namespace MasterDocumentation.Setup;

/// <summary>
/// Обновление, запрошенное самим приложением: папка и режим уже известны, спрашивать нечего.
/// Командная строка — <c>--update --target "&lt;папка&gt;" --mode install|portable --wait &lt;pid&gt;</c>.
/// Установщик дожидается закрытия приложения, распаковывает новую версию поверх старой и
/// запускает её обратно.
/// </summary>
public sealed record UpdateRequest(string TargetDirectory, InstallMode Mode, int? WaitForProcessId, bool Relaunch)
{
    public static UpdateRequest? Parse(IReadOnlyList<string> args)
    {
        if (!args.Any(x => x.Equals("--update", StringComparison.OrdinalIgnoreCase))) return null;
        var target = Value(args, "--target");
        if (string.IsNullOrWhiteSpace(target)) return null;
        var mode = string.Equals(Value(args, "--mode"), "portable", StringComparison.OrdinalIgnoreCase) ? InstallMode.Portable : InstallMode.Install;
        var wait = int.TryParse(Value(args, "--wait"), out var pid) && pid > 0 ? pid : (int?)null;
        var relaunch = !args.Any(x => x.Equals("--no-relaunch", StringComparison.OrdinalIgnoreCase));
        return new(target.Trim(), mode, wait, relaunch);
    }

    /// <summary>Значение параметра в виде «--имя значение» или «--имя=значение».</summary>
    private static string? Value(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (args[index].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return args[index][(name.Length + 1)..].Trim('"');
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
                return args[index + 1].Trim('"');
        }
        return null;
    }
}

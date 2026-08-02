using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using MasterDocumentation.Services;
using Microsoft.Win32;
using MessageBox = MasterDocumentation.Views.MessageBox;

namespace MasterDocumentation.Utilities;

/// <summary>
/// Удаление установленной копии приложения. Запись «Программы и компоненты» создаёт установщик и
/// указывает в ней команду <c>MasterDocumentation.exe --uninstall</c>: отдельный деинсталлятор
/// не нужен, а приложение умеет удалить собственную папку через отложенный скрипт (работающий
/// EXE нельзя удалить, пока процесс не завершился).
/// </summary>
public static class Uninstaller
{
    private const string RegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MasterDocumentation";
    private const string ApplicationName = "MasterDocumentation";

    public static bool IsRequested(IEnumerable<string> args) => args.Any(x => x.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));

    public static void Run(string[] args)
    {
        var silent = args.Any(x => x.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        var installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var dataDirectory = AppPaths.Data;
        var removeData = false;

        if (!silent)
        {
            if (MessageBox.Show($"Удалить MasterDocumentation?\n\nПапка приложения: {installDirectory}", "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            removeData = MessageBox.Show($"Удалить также документацию и настройки?\n\n{dataDirectory}\n\nНажмите «Нет», чтобы сохранить данные для повторной установки.", "Удаление данных", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        RemoveShortcuts();
        RemoveRegistry();

        var directories = new List<string> { installDirectory };
        if (removeData && !IsInside(dataDirectory, installDirectory)) directories.Add(Path.GetFullPath(dataDirectory));
        ScheduleDeletion(directories);

        if (!silent)
            MessageBox.Show("MasterDocumentation будет удалён после закрытия окна.", "Удаление", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static bool IsInside(string candidate, string root)
    {
        try
        {
            var full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void RemoveShortcuts()
    {
        try
        {
            var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ApplicationName + ".lnk");
            if (File.Exists(desktop)) File.Delete(desktop);
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ApplicationName);
            if (Directory.Exists(startMenu)) Directory.Delete(startMenu, true);
        }
        catch (Exception ex) { LogService.Error("Не удалось удалить ярлыки приложения", ex); }
    }

    private static void RemoveRegistry()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(RegistryKey, false); }
        catch (Exception ex) { LogService.Error("Не удалось удалить запись об установке", ex); }
    }

    /// <summary>Папки удаляются скриптом, который дожидается завершения процесса приложения.</summary>
    private static void ScheduleDeletion(IEnumerable<string> directories)
    {
        try
        {
            var script = Path.Combine(Path.GetTempPath(), "masterdocumentation-uninstall-" + Guid.NewGuid().ToString("N") + ".cmd");
            var builder = new StringBuilder();
            builder.AppendLine("@echo off");
            builder.AppendLine("ping -n 4 127.0.0.1 >nul");
            foreach (var directory in directories)
                builder.AppendLine($"rmdir /s /q \"{directory.TrimEnd(Path.DirectorySeparatorChar)}\"");
            builder.AppendLine("del \"%~f0\"");
            File.WriteAllText(script, builder.ToString(), Encoding.GetEncoding(866));
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        }
        catch (Exception ex) { LogService.Error("Не удалось запустить удаление файлов приложения", ex); }
    }
}

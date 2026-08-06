using System.IO;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace MasterDocumentation.Setup;

public enum InstallMode
{
    /// <summary>Установка в папку пользователя с ярлыками и записью в «Программы и компоненты».</summary>
    Install,
    /// <summary>Портативная распаковка: всё рядом с EXE, система не изменяется.</summary>
    Portable,
}

public sealed class InstallOptions
{
    public InstallMode Mode { get; set; } = InstallMode.Install;
    public string TargetDirectory { get; set; } = "";
    public bool DesktopShortcut { get; set; } = true;
    public bool StartMenuShortcut { get; set; } = true;
    public bool LaunchAfterSetup { get; set; } = true;
}

public sealed record InstallProgress(string Text, double Value);

/// <summary>
/// Распаковка приложения и подключение его к системе. Портативный режим ничего не пишет за
/// пределами выбранной папки — это основной сценарий использования приложения; режим установки
/// дополнительно создаёт ярлыки, запись для удаления и выносит данные в профиль пользователя,
/// чтобы приложение работало и в папке, недоступной для записи.
/// </summary>
public static class InstallEngine
{
    public const string ApplicationName = "MasterDocumentation";
    public const string ExecutableName = "MasterDocumentation.exe";
    private const string RegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MasterDocumentation";

    public static string DefaultInstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ApplicationName);
    public static string DefaultPortableDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ApplicationName);
    public static string DefaultDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ApplicationName, "Data");

    /// <summary>Информационная версия: только в ней сохраняется суффикс предрелиза («1.2.0-beta»).</summary>
    public static string Version
    {
        get
        {
            var informational = typeof(InstallEngine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var metadata = informational.IndexOf('+');
                return (metadata >= 0 ? informational[..metadata] : informational).Trim();
            }
            var version = typeof(InstallEngine).Assembly.GetName().Version;
            return version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public static void Run(InstallOptions options, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
    {
        var target = Path.GetFullPath(options.TargetDirectory);
        EnsureWritable(target);

        progress.Report(new("Подготовка файлов…", 0));
        using var archive = PayloadArchive.Open();
        var entries = archive.Entries.Where(entry => !IsDirectoryEntry(entry.FullName)).ToList();
        var prefix = CommonRootFolder(entries);
        var total = Math.Max(1, entries.Count);
        var done = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = entry.FullName;
            if (prefix.Length > 0 && relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                relative = relative[prefix.Length..];
            relative = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            if (relative.Length == 0) continue;

            var destination = Path.GetFullPath(Path.Combine(target, relative));
            if (!destination.StartsWith(target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue; // защита от путей вида ../ внутри архива
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            ExtractWithRetry(entry, destination);
            done++;
            if (done % 25 == 0 || done == total)
                progress.Report(new($"Распаковка файлов… {done} из {total}", done * 92d / total));
        }

        var executable = Path.Combine(target, ExecutableName);
        if (!File.Exists(executable))
            throw new InvalidOperationException("В дистрибутиве не найден " + ExecutableName + ". Скачайте файл установки заново.");

        if (options.Mode == InstallMode.Install)
        {
            progress.Report(new("Настройка хранилища документации…", 94));
            ConfigureDataLocation(target);
            progress.Report(new("Создание ярлыков…", 96));
            CreateShortcuts(options, executable, target);
            progress.Report(new("Регистрация для удаления…", 98));
            RegisterUninstall(target, executable);
        }
        else
        {
            progress.Report(new("Подготовка портативной версии…", 94));
            // Портативная версия хранит данные рядом с собой: файл с расположением не нужен.
            var pointer = Path.Combine(target, "data-location.txt");
            if (File.Exists(pointer)) File.Delete(pointer);
            // Хранилище портативной копии — сама папка приложения: подпапки Assets, Documents,
            // Logs, Backups и Exports создаст приложение при первом запуске.
            if (options.DesktopShortcut) CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ApplicationName + ".lnk"), executable, target);
        }

        progress.Report(new("Готово", 100));
    }

    /// <summary>
    /// При обновлении поверх работавшей копии файл может быть ещё занят — процессы редактора
    /// (WebView2) закрываются с задержкой. Несколько повторов надёжнее, чем прерывать обновление.
    /// </summary>
    private static void ExtractWithRetry(ZipArchiveEntry entry, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                entry.ExtractToFile(destination, true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(400 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                Thread.Sleep(400 * attempt);
            }
        }
    }

    private static void EnsureWritable(string target)
    {
        Directory.CreateDirectory(target);
        var probe = Path.Combine(target, ".setup-write-test");
        try
        {
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException($"Нет прав на запись в папку:\n{target}\n\nВыберите папку в профиле пользователя, например {DefaultInstallDirectory}.");
        }
    }

    private static bool IsDirectoryEntry(string name) => name.Length > 0 && (name[^1] == '/' || name[^1] == '\\');

    /// <summary>
    /// Общая корневая папка архива (MasterDocumentation-vX.Y.Z-win-x64/) при распаковке отбрасывается.
    /// Учитываются оба разделителя: некоторые архиваторы записывают имена с обратным слэшем.
    /// </summary>
    private static string CommonRootFolder(IReadOnlyList<ZipArchiveEntry> entries)
    {
        string? candidate = null;
        foreach (var entry in entries)
        {
            var separator = entry.FullName.IndexOfAny(['/', '\\']);
            if (separator <= 0) return "";
            var root = entry.FullName[..(separator + 1)];
            candidate ??= root;
            if (!string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)) return "";
        }
        return candidate ?? "";
    }

    /// <summary>
    /// Установленная копия держит документацию в профиле пользователя: папка программы
    /// может быть доступна только на чтение, а данные должны переживать обновление.
    /// </summary>
    private static void ConfigureDataLocation(string target)
    {
        var pointer = Path.Combine(target, "data-location.txt");
        if (File.Exists(pointer)) return; // обновление поверх: расположение данных не трогаем
        Directory.CreateDirectory(DefaultDataDirectory);
        File.WriteAllText(pointer, @"%LOCALAPPDATA%\MasterDocumentation\Data");
    }

    private static void CreateShortcuts(InstallOptions options, string executable, string workingDirectory)
    {
        if (options.DesktopShortcut)
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ApplicationName + ".lnk"), executable, workingDirectory);
        if (options.StartMenuShortcut)
        {
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ApplicationName);
            Directory.CreateDirectory(startMenu);
            CreateShortcut(Path.Combine(startMenu, ApplicationName + ".lnk"), executable, workingDirectory);
        }
    }

    private static void CreateShortcut(string linkPath, string executable, string workingDirectory)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return;
            dynamic? shell = Activator.CreateInstance(type);
            if (shell is null) return;
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = executable;
            link.WorkingDirectory = workingDirectory;
            link.IconLocation = executable + ",0";
            link.Description = "Локальная документация MasterDocumentation";
            link.Save();
        }
        catch
        {
            // Ярлык — не критичная часть установки: приложение запускается и без него.
        }
    }

    private static void RegisterUninstall(string target, string executable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryKey);
            if (key is null) return;
            var size = DirectorySize(target) / 1024;
            key.SetValue("DisplayName", ApplicationName);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "MasterDocumentation");
            key.SetValue("InstallLocation", target);
            key.SetValue("DisplayIcon", executable);
            key.SetValue("UninstallString", $"\"{executable}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{executable}\" --uninstall --silent");
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, size), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        }
        catch
        {
            // Запись в «Программы и компоненты» не обязательна для работы приложения.
        }
    }

    private static long DirectorySize(string path)
    {
        try { return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length); }
        catch { return 0; }
    }
}

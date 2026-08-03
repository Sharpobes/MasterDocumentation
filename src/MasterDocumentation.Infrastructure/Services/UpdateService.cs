using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace MasterDocumentation.Services;

/// <summary>Как приложение оказалось на диске: от этого зависят параметры обновления.</summary>
public enum InstallationKind
{
    /// <summary>Установлено установщиком: есть запись в «Программах и компонентах» и ярлыки.</summary>
    Installed,
    /// <summary>Портативная папка: обновляется на месте, система не затрагивается.</summary>
    Portable,
}

public sealed record UpdateRelease(
    Version Version,
    string Tag,
    string InstallerName,
    string InstallerUrl,
    long InstallerSize,
    string? ChecksumUrl,
    string Notes);

/// <summary>
/// Обновление через тот же установщик, которым приложение и ставится: приложение скачивает
/// установщик новой версии, проверяет контрольную сумму и запускает его в режиме обновления —
/// с уже известными папкой и режимом установки, без вопросов пользователю. Скачивать релиз
/// вручную не нужно.
/// </summary>
public static class UpdateService
{
    public const string Repository = "Sharpobes/MasterDocumentation";
    private const string RegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MasterDocumentation";
    private const string UserAgent = "MasterDocumentation-Updater";

    public static Version CurrentVersion
    {
        get
        {
            var version = (Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly).GetName().Version;
            return version is null ? new Version(1, 0, 0) : new Version(version.Major, version.Minor, version.Build);
        }
    }

    public static string InstallDirectory => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>Портативная копия отличается от установленной отсутствием записи об установке в этой папке.</summary>
    public static InstallationKind Kind
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
                var location = key?.GetValue("InstallLocation") as string;
                if (string.IsNullOrWhiteSpace(location)) return InstallationKind.Portable;
                return string.Equals(Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar), InstallDirectory, StringComparison.OrdinalIgnoreCase)
                    ? InstallationKind.Installed
                    : InstallationKind.Portable;
            }
            catch { return InstallationKind.Portable; }
        }
    }

    /// <summary>Возвращает более новый релиз или null, если обновление не нужно или недоступно.</summary>
    public static async Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = CreateClient();
            var json = await client.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken).ConfigureAwait(false);
            var release = ParseRelease(json);
            return release is not null && release.Version > CurrentVersion ? release : null;
        }
        catch (Exception ex)
        {
            LogService.Error("Не удалось проверить обновления", ex);
            return null;
        }
    }

    /// <summary>Разбор ответа GitHub. Релиз без установщика в файлах обновлением не считается.</summary>
    public static UpdateRelease? ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        var tag = root.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() ?? "" : "";
        var version = ParseVersion(tag);
        if (version is null) return null;

        string? installerName = null, installerUrl = null, checksumUrl = null;
        var size = 0L;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? "" : "";
                var url = asset.TryGetProperty("browser_download_url", out var urlValue) ? urlValue.GetString() : null;
                if (url is null) continue;
                if (name.StartsWith("MasterDocumentation-Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    installerName = name;
                    installerUrl = url;
                    size = asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var parsed) ? parsed : 0L;
                }
                else if (name.StartsWith("MasterDocumentation-Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe.sha256", StringComparison.OrdinalIgnoreCase))
                    checksumUrl = url;
            }
        }
        if (installerName is null || installerUrl is null) return null;
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new(version, tag, installerName, installerUrl, size, checksumUrl, notes);
    }

    /// <summary>Версия из тега вида v1.2.0 или 1.2.0-beta; суффикс предрелиза отбрасывается.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var value = tag.Trim().TrimStart('v', 'V');
        var end = value.IndexOfAny(['-', '+']);
        if (end >= 0) value = value[..end];
        var parts = value.Split('.');
        if (parts.Length < 2) return null;
        var numbers = new int[3];
        for (var index = 0; index < 3; index++)
        {
            if (index >= parts.Length) { numbers[index] = 0; continue; }
            if (!int.TryParse(parts[index], out numbers[index])) return null;
        }
        return new Version(numbers[0], numbers[1], numbers[2]);
    }

    /// <summary>
    /// Скачивает установщик во временную папку и сверяет контрольную сумму: запускается
    /// скачанный исполняемый файл, поэтому повреждённая или неполная загрузка не должна
    /// доходить до запуска.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateRelease release, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "MasterDocumentation-Update");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, release.InstallerName);
        using var client = CreateClient();

        using (var response = await client.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.InstallerSize;
            using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[1 << 20];
            long copied = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                if (total > 0) progress?.Report(copied * 100d / total);
            }
        }

        var expected = await ReadChecksumAsync(client, release, cancellationToken).ConfigureAwait(false);
        if (expected is not null && !string.Equals(expected, await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(path);
            throw new InvalidOperationException("Контрольная сумма скачанного установщика не совпала. Попробуйте обновиться ещё раз.");
        }
        return path;
    }

    /// <summary>Запускает установщик в режиме обновления и возвращает управление: приложение должно закрыться.</summary>
    public static void LaunchInstaller(string installerPath)
    {
        var mode = Kind == InstallationKind.Installed ? "install" : "portable";
        var arguments = $"--update --target \"{InstallDirectory}\" --mode {mode} --wait {Environment.ProcessId}";
        Process.Start(new ProcessStartInfo(installerPath, arguments) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Path.GetTempPath() });
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, CurrentVersion.ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static async Task<string?> ReadChecksumAsync(HttpClient client, UpdateRelease release, CancellationToken cancellationToken)
    {
        if (release.ChecksumUrl is null) return null;
        try
        {
            var value = await client.GetStringAsync(release.ChecksumUrl, cancellationToken).ConfigureAwait(false);
            var hash = value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return hash?.Length == 64 ? hash : null;
        }
        catch (Exception ex)
        {
            LogService.Error("Не удалось получить контрольную сумму обновления", ex);
            return null;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* временный файл будет удалён системой */ }
    }
}

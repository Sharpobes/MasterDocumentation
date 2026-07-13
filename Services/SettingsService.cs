using System.Text.Json;
using System.IO;
using MasterDocumentation.Models;
using MasterDocumentation.Utilities;

namespace MasterDocumentation.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public ApplicationSettings Load()
    {
        AppPaths.Ensure();
        if (!File.Exists(AppPaths.Settings)) { var defaults = new ApplicationSettings(); Save(defaults); return defaults; }
        try { return JsonSerializer.Deserialize<ApplicationSettings>(File.ReadAllText(AppPaths.Settings), JsonOptions) ?? new ApplicationSettings(); }
        catch (Exception ex)
        {
            var damaged = AppPaths.Settings + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(AppPaths.Settings, damaged, true); LogService.Error("Повреждён settings.json, загружены безопасные настройки", ex);
            var defaults = new ApplicationSettings(); Save(defaults); return defaults;
        }
    }
    public void Save(ApplicationSettings settings)
    {
        AppPaths.Ensure(); var temp = AppPaths.Settings + ".tmp"; var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(temp, json); _ = JsonSerializer.Deserialize<ApplicationSettings>(File.ReadAllText(temp), JsonOptions) ?? throw new InvalidDataException("Настройки не прошли проверку.");
        File.Move(temp, AppPaths.Settings, true);
    }
    public void Reset() => Save(new ApplicationSettings());
}

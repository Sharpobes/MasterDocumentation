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
        try
        {
            var settings=JsonSerializer.Deserialize<ApplicationSettings>(File.ReadAllText(AppPaths.Settings),JsonOptions)??new ApplicationSettings();
            var changed=false;
            if(settings.Version<2){settings.FirstRunCompleted=File.Exists(AppPaths.Database);settings.Version=2;changed=true;}
            var recent=Math.Clamp(settings.RecentFilesCount,1,100);if(recent!=settings.RecentFilesCount){settings.RecentFilesCount=recent;changed=true;}
            var delay=Math.Clamp(settings.AutoSaveDelaySeconds,1,3600);if(delay!=settings.AutoSaveDelaySeconds){settings.AutoSaveDelaySeconds=delay;changed=true;}
            var limit=Math.Clamp(settings.AutomaticBackupLimit,1,100);if(limit!=settings.AutomaticBackupLimit){settings.AutomaticBackupLimit=limit;changed=true;}
            if(changed)Save(settings);return settings;
        }
        catch (Exception ex)
        {
            var damaged = AppPaths.Settings + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(AppPaths.Settings, damaged, true); LogService.Error("Повреждён settings.json, загружены безопасные настройки", ex);
            var defaults = new ApplicationSettings(); Save(defaults); return defaults;
        }
    }
    public void Save(ApplicationSettings settings)
    {
        settings.Version=2;AppPaths.Ensure(); var temp = AppPaths.Settings + ".tmp"; var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(temp, json); _ = JsonSerializer.Deserialize<ApplicationSettings>(File.ReadAllText(temp), JsonOptions) ?? throw new InvalidDataException("Настройки не прошли проверку.");
        File.Move(temp, AppPaths.Settings, true);
    }
    public void Reset() => Save(new ApplicationSettings());
}

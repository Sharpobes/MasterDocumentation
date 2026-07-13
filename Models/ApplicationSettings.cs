namespace MasterDocumentation.Models;

public sealed class ApplicationSettings
{
    public int Version { get; set; } = 1;
    public string Language { get; set; } = "Русский";
    public string StartupBehavior { get; set; } = "Открывать последнюю сессию";
    public int RecentFilesCount { get; set; } = 10;
    public bool CompactMode { get; set; }
    public bool CheckUpdates { get; set; } = true;
    public bool ShowTooltips { get; set; } = true;
    public bool ConfirmDelete { get; set; } = true;
    public string LinkBehavior { get; set; } = "Во встроенном браузере";
    public string MeasurementUnits { get; set; } = "Миллиметры (мм)";
    public string Theme { get; set; } = "Тёмная";
    public string DefaultFont { get; set; } = "Segoe UI";
    public double DefaultFontSize { get; set; } = 13;
    public bool SpellCheck { get; set; } = true;
    public int AutoSaveDelaySeconds { get; set; } = 2;
    public bool AutomaticBackups { get; set; } = true;
    public int AutomaticBackupLimit { get; set; } = 10;
    public bool EncryptManualBackups { get; set; }
    public List<long> OpenDocumentIds { get; set; } = [];
    public long? SelectedDocumentId { get; set; }
    public Dictionary<string,string> Hotkeys { get; set; } = new()
    {
        ["NewDocument"]="Ctrl+N", ["NewFolder"]="Ctrl+Shift+N", ["Save"]="Ctrl+S", ["Export"]="Ctrl+Shift+S",
        ["Settings"]="Ctrl+OemComma", ["CloseTab"]="Ctrl+W", ["NextTab"]="Ctrl+Tab", ["PreviousTab"]="Ctrl+Shift+Tab"
    };
}

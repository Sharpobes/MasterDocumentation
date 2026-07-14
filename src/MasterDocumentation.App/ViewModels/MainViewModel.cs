using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Documents;
using MasterDocumentation.Models;
using MasterDocumentation.Services;
using MasterDocumentation.Storage;
using Microsoft.Win32;

namespace MasterDocumentation.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    public DatabaseService Database { get; }
    public BackupService Backups { get; }
    public ObservableCollection<NodeItem> Nodes { get; } = [];
    public ObservableCollection<DocumentTab> Tabs { get; } = [];
    private readonly SettingsService _settingsService;private NodeItem? _selectedNode; private DocumentTab? _selectedTab; private string _search = ""; private bool _dark;private string _theme="Тёмная";
    public NodeItem? SelectedNode { get => _selectedNode; set { _selectedNode = value; OnChanged(); } }
    public DocumentTab? SelectedTab { get => _selectedTab; set { _selectedTab = value; OnChanged(); } }
    public string Search { get => _search; set { _search = value; OnChanged(); ReloadTree(); } }
    public bool DarkTheme { get => _dark; set=>SetTheme(value?"Тёмная":"Светлая"); }
    public string Theme=>_theme;
    public MainViewModel(DatabaseService database, BackupService backups,SettingsService settingsService) { Database = database; Backups = backups;_settingsService=settingsService; Database.Initialize();var settings=_settingsService.Load();_theme=settings.Theme;_dark=ResolveDarkTheme(_theme); ReloadTree(); ApplyTheme(); }
    public void ReloadTree() { Nodes.Clear();var expanded=(Database.GetSetting("ExpandedNodeIds")??"").Split(',',StringSplitOptions.RemoveEmptyEntries).Select(x=>long.TryParse(x,out var id)?id:0).Where(x=>x>0).ToHashSet();foreach(var n in Database.LoadTree(Search)){ApplyExpanded(n,expanded);Nodes.Add(n);} }
    private static void ApplyExpanded(NodeItem node,HashSet<long> expanded){node.IsExpanded=expanded.Count==0||expanded.Contains(node.Id);foreach(var child in node.Children)ApplyExpanded(child,expanded);}
    public void ShowAll() { Search = ""; ReloadTree(); }
    public void ShowFavorites() { Nodes.Clear(); foreach (var n in Database.LoadFavorites()) Nodes.Add(n); }
    public void ShowRecent(int limit) { Nodes.Clear(); foreach (var n in Database.LoadRecent(limit)) Nodes.Add(n); }
    public void ShowTrash() { Nodes.Clear(); foreach (var n in Database.LoadTrash()) Nodes.Add(n); }
    public void ShowTemplates() { Nodes.Clear(); foreach (var n in Database.LoadTemplates()) Nodes.Add(n); }
    public void Open(NodeItem node)
    {
        if (node.IsFolder) return; var existing = Tabs.FirstOrDefault(t => t.DocumentId == node.Id); if (existing is not null) { SelectedTab = existing; return; }
        var loaded = Database.LoadDocument(node.Id); var structured=Database.LoadStructuredContent(node.Id); var tab = new DocumentTab { DocumentId = node.Id, Title = node.Title, Document = loaded.Document,EditorJson=structured.Json,Html=structured.Html,PlainText=structured.PlainText, CreatedAt = loaded.Created, ModifiedAt = loaded.Modified }; Tabs.Add(tab); SelectedTab = tab;
    }
    public bool Save(DocumentTab? tab)
    {
        if (tab is null || !tab.IsDirty) return true;
        try { if(!string.IsNullOrWhiteSpace(tab.EditorJson))Database.SaveStructuredContent(tab.DocumentId,tab.EditorJson,tab.Html,tab.PlainText);else Database.SaveDocument(tab.DocumentId, tab.Document, new TextRange(tab.Document.ContentStart, tab.Document.ContentEnd).Text); tab.ModifiedAt = DateTime.Now; tab.IsDirty = false; DraftRecoveryService.Delete(tab.DocumentId); OnChanged(nameof(SelectedTab));return true; }
        catch (Exception ex) { LogService.Error("Ошибка сохранения документа", ex); MessageBox.Show("Не удалось сохранить документ: " + ex.Message, "Ошибка сохранения", MessageBoxButton.OK, MessageBoxImage.Error);return false; }
    }
    public bool SaveAll() { var saved=true;foreach(var tab in Tabs.ToList())saved=Save(tab)&&saved;return saved; }
    public void ApplyTheme()
    {
        if (Application.Current is null) return; var r = Application.Current.Resources;
        r["WindowBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dark ? "#FF111820" : "#FFF7F7F8"));
        r["PanelBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dark ? "#FF171F29" : "#FFFFFFFF"));
        r["ElevatedPanelBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dark ? "#FF1F2935" : "#FFF0F3F6"));
        r["EditorBackgroundBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dark ? "#FF171F29" : "#FFFFFFFF"));
        r["TextBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dark ? "#FFD6DBE4" : "#FF202124"));
        r["BorderBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dark ? "#FF2A3442" : "#FFDADCE0"));
        r["MutedTextBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dark ? "#FF929EAE" : "#FF6B7280"));
        r["SoftAccentBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dark ? "#FF293544" : "#FFE8F3FA"));
        r["AccentBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dark ? "#FF8B63D3" : "#FF206EA5"));
        r["ButtonSurfaceBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dark ? "#FF252F3C" : "#FFF8FAFC"));
        r["AccentHoverBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dark ? "#FFA77AF1" : "#FF185A89"));
        r["ScrollTrackBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dark ? "#FF18212B" : "#FFE7EBEF"));
        r["ScrollThumbBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dark ? "#FF657283" : "#FF9AA5B1"));
    }
    public void SetTheme(string theme,bool save=true){_theme=theme is "Светлая" or "Тёмная" or "Системная"?theme:"Системная";_dark=ResolveDarkTheme(_theme);ApplyTheme();OnChanged(nameof(DarkTheme));OnChanged(nameof(Theme));if(save){var settings=_settingsService.Load();settings.Theme=_theme;_settingsService.Save(settings);}}
    private static bool ResolveDarkTheme(string theme){if(theme=="Тёмная")return true;if(theme=="Светлая")return false;try{return Convert.ToInt32(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",0)??0)==0;}catch{return true;}}
    public event PropertyChangedEventHandler? PropertyChanged; private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}

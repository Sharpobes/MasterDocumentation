using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Documents;
using MasterDocumentation.Models;
using MasterDocumentation.Services;
using MasterDocumentation.Storage;

namespace MasterDocumentation.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    public DatabaseService Database { get; }
    public BackupService Backups { get; }
    public ObservableCollection<NodeItem> Nodes { get; } = [];
    public ObservableCollection<DocumentTab> Tabs { get; } = [];
    private NodeItem? _selectedNode; private DocumentTab? _selectedTab; private string _search = ""; private bool _dark;
    public NodeItem? SelectedNode { get => _selectedNode; set { _selectedNode = value; OnChanged(); } }
    public DocumentTab? SelectedTab { get => _selectedTab; set { _selectedTab = value; OnChanged(); } }
    public string Search { get => _search; set { _search = value; OnChanged(); ReloadTree(); } }
    public bool DarkTheme { get => _dark; set { _dark = value; OnChanged(); ApplyTheme(); Database.SetSetting("DarkTheme", value.ToString()); } }
    public MainViewModel(DatabaseService database, BackupService backups) { Database = database; Backups = backups; Database.Initialize(); _dark = true; ReloadTree(); ApplyTheme(); }
    public void ReloadTree() { Nodes.Clear(); foreach (var n in Database.LoadTree(Search)) Nodes.Add(n); }
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
    public void Save(DocumentTab? tab)
    {
        if (tab is null || !tab.IsDirty) return;
        try { if(!string.IsNullOrWhiteSpace(tab.EditorJson))Database.SaveStructuredContent(tab.DocumentId,tab.EditorJson,tab.Html,tab.PlainText);else Database.SaveDocument(tab.DocumentId, tab.Document, new TextRange(tab.Document.ContentStart, tab.Document.ContentEnd).Text); tab.ModifiedAt = DateTime.Now; tab.IsDirty = false; OnChanged(nameof(SelectedTab)); }
        catch (Exception ex) { LogService.Error("Ошибка сохранения документа", ex); MessageBox.Show("Не удалось сохранить документ: " + ex.Message, "Ошибка сохранения", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    public void SaveAll() { foreach (var tab in Tabs.ToList()) Save(tab); }
    public void ApplyTheme()
    {
        if (Application.Current is null) return; var r = Application.Current.Resources;
        r["WindowBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dark ? "#FF111820" : "#FFF7F7F8"));
        r["PanelBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dark ? "#FF171F29" : "#FFFFFFFF"));
        r["TextBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dark ? "#FFD6DBE4" : "#FF202124"));
        r["BorderBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dark ? "#FF2A3442" : "#FFDADCE0"));
        r["MutedTextBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dark ? "#FF929EAE" : "#FF6B7280"));
        r["SoftAccentBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dark ? "#FF293544" : "#FFE8F3FA"));
        r["AccentBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dark ? "#FF8B63D3" : "#FF206EA5"));
        r["ButtonSurfaceBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dark ? "#FF252F3C" : "#FFF8FAFC"));
    }
    public event PropertyChangedEventHandler? PropertyChanged; private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}

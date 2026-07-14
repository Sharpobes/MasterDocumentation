using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Documents;

namespace MasterDocumentation.Models;

public sealed class DocumentTab : INotifyPropertyChanged
{
    private string _title = "";
    private bool _dirty;
    public long DocumentId { get; init; }
    public string Title { get => _title; set { _title = value; OnChanged(); } }
    public FlowDocument Document { get; set; } = new();
    public string EditorJson { get; set; } = "";
    public string Html { get; set; } = "";
    public string PlainText { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public bool IsDirty { get => _dirty; set { _dirty = value; OnChanged(); OnChanged(nameof(DisplayTitle)); } }
    public string DisplayTitle => IsDirty ? Title + " •" : Title;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

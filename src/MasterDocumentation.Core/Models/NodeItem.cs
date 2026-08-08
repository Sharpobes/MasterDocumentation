using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MasterDocumentation.Models;

public sealed class NodeItem : INotifyPropertyChanged
{
    private string _title = "";
    private bool _isExpanded = true;
    public long Id { get; set; }
    public long? ParentId { get; set; }
    public bool IsFolder { get; set; }
    /// <summary>Документ-шаблон: в списке документов не показывается, для него есть раздел «Шаблоны».</summary>
    public bool IsTemplate { get; set; }
    public string Title { get => _title; set { _title = value; OnChanged(); } }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public bool IsExpanded { get => _isExpanded; set { _isExpanded = value; OnChanged(); } }
    public ObservableCollection<NodeItem> Children { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

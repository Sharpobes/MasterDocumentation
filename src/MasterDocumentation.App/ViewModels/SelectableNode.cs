using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MasterDocumentation.Models;

namespace MasterDocumentation.ViewModels;

/// <summary>
/// Узел дерева документации с флажком выбора — используется в окне обмена с базой данных,
/// чтобы отметить, какие именно страницы сохранять в базу или забирать из неё.
/// Отметка папки распространяется на всё её содержимое, состояние папки пересчитывается
/// по дочерним элементам (частичный выбор — неопределённое состояние флажка).
/// </summary>
public sealed class SelectableNode : INotifyPropertyChanged
{
    private bool? _isChecked = false;
    private bool _updating;

    public long Id { get; }
    public string Title { get; }
    public bool IsFolder { get; }
    public bool IsPrivate { get; }
    public string Owner { get; }
    public SelectableNode? Parent { get; private set; }
    public ObservableCollection<SelectableNode> Children { get; } = [];
    public bool IsExpanded { get; set; } = true;

    public string Badge => IsFolder ? "" : IsPrivate ? "приватный · " + Owner : Owner.Length > 0 ? Owner : "общий";

    public bool? IsChecked
    {
        get => _isChecked;
        set => SetChecked(value ?? false, true, true);
    }

    public SelectableNode(NodeItem node, Func<long, DocumentAccess?> accessLookup)
    {
        Id = node.Id;
        Title = node.Title;
        IsFolder = node.IsFolder;
        var access = node.IsFolder ? null : accessLookup(node.Id);
        IsPrivate = access?.IsPrivate ?? false;
        Owner = access?.Owner ?? "";
        foreach (var child in node.Children)
        {
            var item = new SelectableNode(child, accessLookup) { Parent = this };
            Children.Add(item);
        }
    }

    public void SetChecked(bool value, bool updateChildren, bool updateParent)
    {
        if (_updating) return;
        _updating = true;
        _isChecked = value;
        if (updateChildren) foreach (var child in Children) child.SetChecked(value, true, false);
        _updating = false;
        if (updateParent) Parent?.RefreshFromChildren();
        OnChanged(nameof(IsChecked));
    }

    private void RefreshFromChildren()
    {
        if (Children.Count == 0) return;
        var checkedCount = Children.Count(x => x.IsChecked == true);
        var indeterminate = Children.Any(x => x.IsChecked is null);
        _isChecked = indeterminate || (checkedCount > 0 && checkedCount < Children.Count) ? null : checkedCount == Children.Count;
        OnChanged(nameof(IsChecked));
        Parent?.RefreshFromChildren();
    }

    public IEnumerable<SelectableNode> Flatten()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var item in child.Flatten())
                yield return item;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

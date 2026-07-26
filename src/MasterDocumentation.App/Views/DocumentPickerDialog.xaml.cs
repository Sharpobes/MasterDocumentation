using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MasterDocumentation.Models;
using MasterDocumentation.Storage;

namespace MasterDocumentation.Views;

public sealed record DocumentChoice(long Id,string Guid,string Title,string Display);

public partial class DocumentPickerDialog : Window
{
    private readonly List<DocumentChoice> _items=[];
    public DocumentChoice? SelectedDocument=>DocumentsList.SelectedItem as DocumentChoice;

    public DocumentPickerDialog(DatabaseService database)
    {
        InitializeComponent();
        Add(database,database.LoadTree(),"");
        DocumentsList.ItemsSource=_items;
        Loaded+=(_,_)=>SearchBox.Focus();
    }

    private void Add(DatabaseService database,IEnumerable<NodeItem> nodes,string path)
    {
        foreach(var node in nodes)
        {
            var current=string.IsNullOrEmpty(path)?node.Title:path+" / "+node.Title;
            if(node.IsFolder)Add(database,node.Children,current);
            else _items.Add(new(node.Id,database.GetDocumentGuid(node.Id),node.Title,current));
        }
    }

    private void Search_Changed(object sender,TextChangedEventArgs e)
    {
        var query=SearchBox.Text.Trim();
        var filtered=string.IsNullOrEmpty(query)?_items:_items.Where(x=>x.Display.Contains(query,StringComparison.CurrentCultureIgnoreCase)).ToList();
        DocumentsList.ItemsSource=filtered;
        DocumentsEmptyState.Visibility=filtered.Count==0?Visibility.Visible:Visibility.Collapsed;
    }

    private void Select_Click(object sender,RoutedEventArgs e){if(SelectedDocument is null){MessageBox.Show(this,"Выберите документ.");return;}DialogResult=true;}
    private void Documents_DoubleClick(object sender,MouseButtonEventArgs e){if(SelectedDocument is not null)DialogResult=true;}
}

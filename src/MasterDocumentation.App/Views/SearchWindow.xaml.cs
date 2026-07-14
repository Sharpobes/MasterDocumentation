using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MasterDocumentation.Models;
using MasterDocumentation.Storage;

namespace MasterDocumentation.Views;

public partial class SearchWindow : Window
{
    private readonly DatabaseService _database;
    private readonly DispatcherTimer _debounce;
    public long? SelectedDocumentId=>(ResultsList.SelectedItem as DocumentSearchResult)?.Id;

    public SearchWindow(DatabaseService database,string initialQuery="")
    {
        InitializeComponent();_database=database;_debounce=new(){Interval=TimeSpan.FromMilliseconds(250)};_debounce.Tick+=(_,_)=>{_debounce.Stop();Search();};QueryBox.Text=initialQuery;Loaded+=(_,_)=>{QueryBox.Focus();QueryBox.SelectAll();Search();};
    }

    private static string Selected(ComboBox box)=>(box.SelectedItem as ComboBoxItem)?.Content?.ToString()??"";
    private void Filter_Changed(object sender,RoutedEventArgs e){if(!IsLoaded)return;_debounce.Stop();_debounce.Start();}
    private void Search_Click(object sender,RoutedEventArgs e)=>Search();
    private void Search()
    {
        try{var values=_database.SearchDocuments(QueryBox.Text,Selected(StatusBox),TagBox.Text,FavoritesBox.IsChecked==true,AttachmentsBox.IsChecked==true,TrashBox.IsChecked==true,Selected(SortBox)=="По дате");ResultsList.ItemsSource=values;ResultCountText.Text=$"Найдено: {values.Count}";}
        catch(Exception ex){ResultsList.ItemsSource=null;ResultCountText.Text="Не удалось выполнить запрос: "+ex.Message;}
    }
    private void Open_Click(object sender,RoutedEventArgs e){if(SelectedDocumentId is null){MessageBox.Show(this,"Выберите документ.");return;}DialogResult=true;}
    private void Results_DoubleClick(object sender,MouseButtonEventArgs e){if(SelectedDocumentId is not null)DialogResult=true;}
}

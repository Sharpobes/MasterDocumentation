using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using MasterDocumentation.Models;
using MasterDocumentation.Storage;

namespace MasterDocumentation.Views;

public sealed class EditableProperty : INotifyPropertyChanged
{
    private string _name="";private string _value="";
    public string Name{get=>_name;set{_name=value;PropertyChanged?.Invoke(this,new(nameof(Name)));}}
    public string Value{get=>_value;set{_value=value;PropertyChanged?.Invoke(this,new(nameof(Value)));}}
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class DocumentPropertiesWindow : Window
{
    private readonly DatabaseService _database;private readonly long _documentId;private readonly ObservableCollection<EditableProperty> _properties=[];
    public DocumentPropertiesWindow(DatabaseService database,long documentId)
    {
        InitializeComponent();_database=database;_documentId=documentId;PropertiesGrid.ItemsSource=_properties;LoadData();
    }
    private void LoadData()
    {
        var metadata=_database.GetDocumentMetadata(_documentId);TitleText.Text=metadata.Title;MetadataLeft.Text=$"GUID: {metadata.Guid}\nСоздан: {metadata.CreatedAt:g}\nИзменён: {metadata.ModifiedAt:g}\nАвтор: {metadata.Author}";MetadataRight.Text=$"Родитель: {Value(metadata.ParentTitle)}\nШаблон: {Value(metadata.TemplateTitle)}\nСлов: {metadata.WordCount}\nСимволов: {metadata.CharacterCount}\nВложений: {metadata.AttachmentCount}";ReloadStatuses(metadata.Status);
        var access=_database.GetDocumentAccess(_documentId);PrivateCheck.IsChecked=access.IsPrivate;
        var owner=string.IsNullOrWhiteSpace(access.Owner)?Utilities.UserIdentity.Current:access.Owner;
        var mine=access.IsOwnedBy(Utilities.UserIdentity.Current)||string.IsNullOrWhiteSpace(access.Owner);
        PrivateCheck.IsEnabled=mine;
        AccessHintText.Text=mine
            ?$"Владелец: {owner}. Обычную страницу видят все, кто подключился к этой базе данных через приложение."
            :$"Владелец: {owner}. Изменить доступ может только владелец страницы.";
        _properties.Clear();foreach(var property in _database.GetCustomProperties(_documentId))_properties.Add(new(){Name=property.Name,Value=property.Value});
    }
    private static string Value(string value)=>string.IsNullOrWhiteSpace(value)?"—":value;
    private void ReloadStatuses(string? selected=null){var value=selected??StatusBox.SelectedItem?.ToString();StatusBox.ItemsSource=_database.GetStatuses();StatusBox.SelectedItem=value;StatusBox.SelectedIndex=StatusBox.SelectedIndex<0?0:StatusBox.SelectedIndex;}
    private void AddStatus_Click(object sender,RoutedEventArgs e){var prompt=new TextPrompt("Новый статус",""){Owner=this};if(prompt.ShowDialog()!=true)return;try{_database.AddStatus(prompt.Value);ReloadStatuses(prompt.Value.Trim());}catch(Exception ex){MessageBox.Show(this,ex.Message,"Статусы",MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private void DeleteStatus_Click(object sender,RoutedEventArgs e){if(StatusBox.SelectedItem is not string status)return;if(MessageBox.Show(this,$"Удалить статус «{status}»?","Статусы",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;try{_database.DeleteStatus(status);ReloadStatuses();}catch(Exception ex){MessageBox.Show(this,ex.Message,"Статусы",MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private void AddProperty_Click(object sender,RoutedEventArgs e){var item=new EditableProperty();_properties.Add(item);PropertiesGrid.SelectedItem=item;PropertiesGrid.ScrollIntoView(item);PropertiesGrid.BeginEdit();}
    private void RemoveProperty_Click(object sender,RoutedEventArgs e){if(PropertiesGrid.SelectedItem is EditableProperty property)_properties.Remove(property);}
    private void Save_Click(object sender,RoutedEventArgs e){try{PropertiesGrid.CommitEdit();_database.SetStatus(_documentId,StatusBox.SelectedItem?.ToString()??"Черновик");if(PrivateCheck.IsEnabled)_database.SetDocumentAccess(_documentId,PrivateCheck.IsChecked==true);_database.SetCustomProperties(_documentId,_properties.Select(x=>new CustomPropertyValue(x.Name,x.Value)));DialogResult=true;}catch(Exception ex){MessageBox.Show(this,ex.Message,"Свойства",MessageBoxButton.OK,MessageBoxImage.Warning);}}
}

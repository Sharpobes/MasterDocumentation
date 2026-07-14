using System.Windows;
using MasterDocumentation.Models;
using MasterDocumentation.Storage;

namespace MasterDocumentation.Views;

public partial class HistoryWindow : Window
{
    private readonly DatabaseService _database;
    private readonly long _documentId;
    public HistoryWindow(DatabaseService database,long documentId){InitializeComponent();_database=database;_documentId=documentId;CurrentText.Text=_database.GetDocumentPlainText(documentId);Reload();}
    private void Reload(){VersionsList.ItemsSource=_database.GetVersions(_documentId);PreviewText.Text="";}
    private void VersionsList_SelectionChanged(object sender,System.Windows.Controls.SelectionChangedEventArgs e){PreviewText.Text=VersionsList.SelectedItem is DocumentVersion version?_database.GetVersionPlainText(version.Id):"";}
    private void Restore_Click(object sender,RoutedEventArgs e){if(VersionsList.SelectedItem is not DocumentVersion version){MessageBox.Show(this,"Выберите версию.");return;}if(MessageBox.Show(this,"Текущее содержимое будет заменено выбранной версией.","Восстановление",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;_database.RestoreVersion(version.Id);DialogResult=true;}
    private void DeleteVersion_Click(object sender,RoutedEventArgs e){if(VersionsList.SelectedItem is not DocumentVersion version){MessageBox.Show(this,"Выберите версию.");return;}if(MessageBox.Show(this,"Удалить выбранную версию без возможности восстановления?","История версий",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;_database.DeleteVersion(version.Id);Reload();}
    private void TogglePin_Click(object sender,RoutedEventArgs e){if(VersionsList.SelectedItem is not DocumentVersion version){MessageBox.Show(this,"Выберите версию.");return;}_database.ToggleVersionPin(version.Id);Reload();}
}

using System.Windows;
using MasterDocumentation.Models;
using MasterDocumentation.Storage;

namespace MasterDocumentation.Views;

public partial class HistoryWindow : Window
{
    private readonly DatabaseService _database;
    private readonly long _documentId;
    public HistoryWindow(DatabaseService database,long documentId){InitializeComponent();_database=database;_documentId=documentId;CurrentText.Text=_database.GetDocumentPlainText(documentId);Reload();}
    private void Reload(){var versions=_database.GetVersions(_documentId);VersionsList.ItemsSource=versions;EmptyState.Visibility=versions.Count==0?Visibility.Visible:Visibility.Collapsed;PreviewText.Text="";UpdateActions();}
    private void VersionsList_SelectionChanged(object sender,System.Windows.Controls.SelectionChangedEventArgs e){PreviewText.Text=VersionsList.SelectedItem is DocumentVersion version?_database.GetVersionPlainText(version.Id):"";UpdateActions();}
    private void UpdateActions(){var enabled=VersionsList.SelectedItem is DocumentVersion;PinButton.IsEnabled=enabled;DeleteButton.IsEnabled=enabled;RestoreButton.IsEnabled=enabled;}
    private void Restore_Click(object sender,RoutedEventArgs e){if(VersionsList.SelectedItem is not DocumentVersion version){MessageBox.Show(this,"Выберите версию.");return;}if(MessageBox.Show(this,"Текущее содержимое будет заменено выбранной версией.","Восстановление",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;_database.RestoreVersion(version.Id);DialogResult=true;}
    private void DeleteVersion_Click(object sender,RoutedEventArgs e){if(VersionsList.SelectedItem is not DocumentVersion version){MessageBox.Show(this,"Выберите версию.");return;}if(MessageBox.Show(this,"Удалить выбранную версию без возможности восстановления?","История версий",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;_database.DeleteVersion(version.Id);Reload();}
    private void TogglePin_Click(object sender,RoutedEventArgs e){if(VersionsList.SelectedItem is not DocumentVersion version){MessageBox.Show(this,"Выберите версию.");return;}_database.ToggleVersionPin(version.Id);Reload();}
}

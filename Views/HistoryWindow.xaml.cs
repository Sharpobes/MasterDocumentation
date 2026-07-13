using System.Windows;
using MasterDocumentation.Models;
using MasterDocumentation.Storage;

namespace MasterDocumentation.Views;

public partial class HistoryWindow : Window
{
    private readonly DatabaseService _database;
    public HistoryWindow(DatabaseService database,long documentId){InitializeComponent();_database=database;VersionsList.ItemsSource=database.GetVersions(documentId);}
    private void Restore_Click(object sender,RoutedEventArgs e){if(VersionsList.SelectedItem is not DocumentVersion version){MessageBox.Show(this,"Выберите версию.");return;}if(MessageBox.Show(this,"Текущее содержимое будет заменено выбранной версией.","Восстановление",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;_database.RestoreVersion(version.Id);DialogResult=true;}
}

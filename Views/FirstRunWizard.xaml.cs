using System.IO;
using System.Windows;
using Microsoft.Win32;
using MasterDocumentation.Utilities;

namespace MasterDocumentation.Views;

public partial class FirstRunWizard : Window
{
    public string SelectedLanguage { get; private set; }="Русский";
    public string SelectedTheme { get; private set; }="Тёмная";
    public string SelectedDataPath { get; private set; }=AppPaths.Data;
    public bool AutomaticBackups { get; private set; }=true;
    public string? FirstDocumentTitle { get; private set; }

    public FirstRunWizard(){InitializeComponent();DataPathBox.Text=AppPaths.Data;}
    private void Browse_Click(object sender,RoutedEventArgs e){var dialog=new OpenFolderDialog{Title="Выберите папку для хранилища MasterDocumentation"};if(dialog.ShowDialog(this)==true)DataPathBox.Text=Path.Combine(dialog.FolderName,"MasterDocumentationData");}
    private void CreateDocument_Changed(object sender,RoutedEventArgs e){if(FirstDocumentBox is not null)FirstDocumentBox.IsEnabled=CreateDocumentCheck.IsChecked==true;}
    private void Skip_Click(object sender,RoutedEventArgs e){SelectedLanguage="Русский";SelectedTheme="Тёмная";SelectedDataPath=AppPaths.Data;AutomaticBackups=true;FirstDocumentTitle=null;DialogResult=true;}
    private void Finish_Click(object sender,RoutedEventArgs e)
    {
        var path=DataPathBox.Text.Trim();if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path)){MessageBox.Show(this,"Выберите абсолютный путь к папке данных.","Первый запуск",MessageBoxButton.OK,MessageBoxImage.Warning);return;}if(CreateDocumentCheck.IsChecked==true&&string.IsNullOrWhiteSpace(FirstDocumentBox.Text)){MessageBox.Show(this,"Введите название первого документа.","Первый запуск",MessageBoxButton.OK,MessageBoxImage.Warning);return;}
        try{Directory.CreateDirectory(path);var probe=Path.Combine(path,".write-test-"+Guid.NewGuid().ToString("N"));File.WriteAllText(probe,"ok");File.Delete(probe);}catch(Exception ex){MessageBox.Show(this,"Выбранная папка недоступна для записи:\n"+ex.Message,"Первый запуск",MessageBoxButton.OK,MessageBoxImage.Error);return;}
        SelectedLanguage=(LanguageBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString()??"Русский";SelectedTheme=(ThemeBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString()??"Тёмная";SelectedDataPath=Path.GetFullPath(path);AutomaticBackups=AutomaticBackupsCheck.IsChecked==true;FirstDocumentTitle=CreateDocumentCheck.IsChecked==true?FirstDocumentBox.Text.Trim():null;DialogResult=true;
    }
}

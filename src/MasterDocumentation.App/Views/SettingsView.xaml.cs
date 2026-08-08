using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using MasterDocumentation.Models;
using MasterDocumentation.Services;
using MasterDocumentation.Storage;
using MasterDocumentation.Utilities;
using MasterDocumentation.UI;

namespace MasterDocumentation.Views;

public partial class SettingsView : UserControl
{
    private readonly DatabaseService _database;
    private readonly BackupService _backups;
    private readonly SettingsService _settingsService;
    private readonly FrameworkElement[] _panels;
    private ApplicationSettings _settings;
    private StorageProviderConfig _storageConfig=StorageConfigService.Load();
    private List<HotkeySetting> _hotkeys=[];
    private bool _loading=true;
    private bool _dirty;
    private readonly string _initialLanguage;
    /// <summary>Файлы, которые не доехали при последнем переносе: их можно доложить отдельной кнопкой.</summary>
    private List<string> _pendingAssets=[];
    public event Action<ApplicationSettings>? SettingsSaved;
    public event Action? CloseRequested;

    public SettingsView(DatabaseService database,BackupService backups,SettingsService settingsService)
    {
        InitializeComponent();_database=database;_backups=backups;_settingsService=settingsService;_settings=_settingsService.Load();_initialLanguage=_settings.Language;
        _panels=[GeneralPanel,StoragePanel,EditorPanel,InterfacePanel,DocumentsPanel,HotkeysPanel,SecurityPanel,AboutPanel];
        DefaultFontBox.ItemsSource=Fonts.SystemFontFamilies.OrderBy(x=>x.Source);LoadValues();LoadStatistics();ShowPanel(0,"Общие");_loading=false;
    }

    private void LoadValues()
    {
        SelectCombo(LanguageBox,_settings.Language);SelectCombo(StartupBox,_settings.StartupBehavior);RecentCountText.Text=_settings.RecentFilesCount.ToString();CompactCheck.IsChecked=_settings.CompactMode;UpdatesCheck.IsChecked=_settings.CheckUpdates;TooltipsCheck.IsChecked=_settings.ShowTooltips;ConfirmDeleteCheck.IsChecked=_settings.ConfirmDelete;SelectCombo(LinkBox,_settings.LinkBehavior);SelectCombo(UnitsBox,_settings.MeasurementUnits);
        DefaultFontBox.SelectedItem=Fonts.SystemFontFamilies.FirstOrDefault(x=>x.Source==_settings.DefaultFont);DefaultFontSizeBox.Text=_settings.DefaultFontSize.ToString();SpellCheckBox.IsChecked=_settings.SpellCheck;AutoSaveDelayBox.Text=_settings.AutoSaveDelaySeconds.ToString();SelectCombo(ThemeBox,_settings.Theme);EncryptionCheck.IsChecked=_settings.EncryptManualBackups;
        DataPathBox.Text=AppPaths.Data;AssetsPathText.Text=AppPaths.Assets;BackupsPathText.Text=AppPaths.Backups;ExportsPathText.Text=AppPaths.Exports;DocumentsPathText.Text=AppPaths.Documents;StorageUserBox.Text=UserIdentity.Current;RuntimeText.Text=$"Версия .NET: {Environment.Version}";AppPathText.Text="Путь приложения: "+AppContext.BaseDirectory;LoadHotkeys();
        StorageProviderPostgresRadio.IsChecked=_storageConfig.Provider==StorageProviderKind.Postgres;StorageProviderSqliteRadio.IsChecked=_storageConfig.Provider!=StorageProviderKind.Postgres;PostgresConnectionStringBox.Text=_storageConfig.PostgresConnectionString;PostgresConfigPanel.Visibility=_storageConfig.Provider==StorageProviderKind.Postgres?Visibility.Visible:Visibility.Collapsed;StorageProviderStatusText.Text="";LoadPendingAssets();
        _dirty=false;SaveButton.IsEnabled=false;ApplyButton.IsEnabled=false;
    }

    private void LoadStatistics()
    {
        var dataBytes=StorageSize();var dbBytes=File.Exists(AppPaths.Database)?new FileInfo(AppPaths.Database).Length:0;var assetsBytes=DirectorySize(AppPaths.Assets);var attachments=Directory.Exists(AppPaths.Assets)?Directory.EnumerateFiles(AppPaths.Assets,"*",SearchOption.AllDirectories).LongCount():0;var drive=new DriveInfo(Path.GetPathRoot(AppPaths.Data)!);var used=drive.TotalSize==0?0:(double)(drive.TotalSize-drive.AvailableFreeSpace)/drive.TotalSize*100;
        StoragePathText.Text=AppPaths.Data;StorageStatsText.Text=$"Данные: {FormatBytes(dataBytes)}\nБаза: {FormatBytes(dbBytes)}\nВложения: {FormatBytes(assetsBytes)} ({attachments})\nДокументы: {_database.CountDocuments()}\nДиск занят на {used:F0}%";DiskUsageBar.Value=used;
        var last=Directory.Exists(AppPaths.Backups)?Directory.EnumerateFiles(AppPaths.Backups).Select(x=>new FileInfo(x)).OrderByDescending(x=>x.LastWriteTimeUtc).FirstOrDefault():null;LastBackupText.Text=last is null?"Резервных копий ещё нет":$"Последняя копия: {last.LastWriteTime:g}, {FormatBytes(last.Length)}";
    }

    /// <summary>
    /// Тему можно переключить и мимо настроек — кнопкой в строке состояния. Открытая страница
    /// настроек про это не знает, и «Применить» вернуло бы старое значение из своего списка,
    /// поэтому выбранное значение синхронизируется здесь и не считается правкой пользователя.
    /// </summary>
    public void SyncTheme(string theme)
    {
        if(_settings.Theme==theme)return;
        var wasLoading=_loading;_loading=true;
        _settings.Theme=theme;SelectCombo(ThemeBox,theme);
        _loading=wasLoading;
    }

    private static void SelectCombo(ComboBox box,string value)=>box.SelectedItem=box.Items.OfType<ComboBoxItem>().FirstOrDefault(x=>LocalizationService.SourceContent(x)==value||x.Content?.ToString()==value);
    private static string Selected(ComboBox box)=>box.SelectedItem is ComboBoxItem item?LocalizationService.SourceContent(item):box.Text;
    private void ShowPanel(int index,string title)
    {
        for(var i=0;i<_panels.Length;i++)
            _panels[i].Visibility=i==index?Visibility.Visible:Visibility.Collapsed;
        SectionTitle.Text=title;
        foreach(var button in SettingsNavigationItems.Children.OfType<Button>())
        {
            var selected=int.TryParse(button.Tag?.ToString(),out var buttonIndex)&&buttonIndex==index;
            if(selected)
            {
                button.SetResourceReference(Button.BackgroundProperty,"Accent/Soft");
                button.SetResourceReference(Button.BorderBrushProperty,"Accent/Primary");
            }
            else
            {
                button.ClearValue(Button.BackgroundProperty);
                button.ClearValue(Button.BorderBrushProperty);
            }
        }
    }
    private void SettingsSearch_TextChanged(object sender,TextChangedEventArgs e)
    {
        if(SettingsNavigationItems is null)return;
        var query=SettingsSearchBox.Text.Trim();
        foreach(var button in SettingsNavigationItems.Children.OfType<Button>())
        {
            var searchable=string.Join(" ",FindVisualChildren<TextBlock>(button).Select(text=>text.Text));
            button.Visibility=string.IsNullOrWhiteSpace(query)||searchable.Contains(query,StringComparison.CurrentCultureIgnoreCase)
                ?Visibility.Visible
                :Visibility.Collapsed;
        }
    }
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);
            if(child is T match)yield return match;
            foreach(var descendant in FindVisualChildren<T>(child))yield return descendant;
        }
    }
    private void General_Click(object sender,RoutedEventArgs e)=>ShowPanel(0,"Общие");
    private void Storage_Click(object sender,RoutedEventArgs e)=>ShowPanel(1,"Хранение");
    private void EditorSettings_Click(object sender,RoutedEventArgs e)=>ShowPanel(2,"Редактор");
    private void Interface_Click(object sender,RoutedEventArgs e)=>ShowPanel(3,"Интерфейс");
    private void Documents_Click(object sender,RoutedEventArgs e)=>ShowPanel(4,"Документы и экспорт");
    private void Hotkeys_Click(object sender,RoutedEventArgs e)=>ShowPanel(5,"Горячие клавиши");
    private void Security_Click(object sender,RoutedEventArgs e)=>ShowPanel(6,"Безопасность и приватность");
    private void About_Click(object sender,RoutedEventArgs e)=>ShowPanel(7,"О программе");

    private void Control_Changed(object sender,RoutedEventArgs e)=>MarkDirty();
    private void Control_Changed(object sender,TextChangedEventArgs e)=>MarkDirty();
    private void Control_Changed(object sender,SelectionChangedEventArgs e)=>MarkDirty();
    private void Language_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(_loading||LanguageBox.SelectedItem is not ComboBoxItem item)return;
        LocalizationService.SetLanguage(LocalizationService.SourceContent(item));
        if(Window.GetWindow(this) is DependencyObject root)LocalizationService.Apply(root);else LocalizationService.Apply(this);
        MarkDirty();
    }
    private void MarkDirty(){if(_loading)return;_dirty=true;SaveButton.IsEnabled=true;ApplyButton.IsEnabled=true;}
    private void RecentMinus_Click(object sender,RoutedEventArgs e){RecentCountText.Text=Math.Max(1,int.Parse(RecentCountText.Text)-1).ToString();MarkDirty();}
    private void RecentPlus_Click(object sender,RoutedEventArgs e){RecentCountText.Text=Math.Min(100,int.Parse(RecentCountText.Text)+1).ToString();MarkDirty();}

    private bool SaveSettings()
    {
        if(!double.TryParse(DefaultFontSizeBox.Text,out var fontSize)||fontSize is <6 or >200){MessageBox.Show(Window.GetWindow(this),"Размер шрифта должен быть от 6 до 200.","Настройки");return false;}
        if(!int.TryParse(AutoSaveDelayBox.Text,out var delay)||delay is <1 or >3600){MessageBox.Show(Window.GetWindow(this),"Задержка автосохранения должна быть от 1 до 3600 секунд.","Настройки");return false;}
        HotkeysGrid.CommitEdit(DataGridEditingUnit.Cell,true);HotkeysGrid.CommitEdit(DataGridEditingUnit.Row,true);if(!ValidateHotkeys())return false;
        var newProvider=StorageProviderPostgresRadio.IsChecked==true?StorageProviderKind.Postgres:StorageProviderKind.Sqlite;
        if(newProvider==StorageProviderKind.Postgres&&string.IsNullOrWhiteSpace(PostgresConnectionStringBox.Text)){MessageBox.Show(Window.GetWindow(this),"Укажите строку подключения к PostgreSQL или выберите SQLite.","Настройки");return false;}
        var providerChanged=newProvider!=_storageConfig.Provider||PostgresConnectionStringBox.Text.Trim()!=_storageConfig.PostgresConnectionString;
        _settings.Language=Selected(LanguageBox);_settings.StartupBehavior=Selected(StartupBox);_settings.RecentFilesCount=int.Parse(RecentCountText.Text);_settings.CompactMode=CompactCheck.IsChecked==true;_settings.CheckUpdates=UpdatesCheck.IsChecked==true;_settings.ShowTooltips=TooltipsCheck.IsChecked==true;_settings.ConfirmDelete=ConfirmDeleteCheck.IsChecked==true;_settings.LinkBehavior=Selected(LinkBox);_settings.MeasurementUnits=Selected(UnitsBox);_settings.DefaultFont=(DefaultFontBox.SelectedItem as FontFamily)?.Source??DefaultFontBox.Text;_settings.DefaultFontSize=fontSize;_settings.SpellCheck=SpellCheckBox.IsChecked==true;_settings.AutoSaveDelaySeconds=delay;_settings.Theme=Selected(ThemeBox);_settings.EncryptManualBackups=EncryptionCheck.IsChecked==true;_settings.Hotkeys=_hotkeys.ToDictionary(x=>x.Id,x=>x.Gesture);
        _settingsService.Save(_settings);
        _storageConfig=new StorageProviderConfig{Provider=newProvider,PostgresConnectionString=PostgresConnectionStringBox.Text.Trim()};StorageConfigService.Save(_storageConfig);
        if(StorageUserBox.Text.Trim().Length>0&&StorageUserBox.Text.Trim()!=UserIdentity.Current)UserIdentity.Set(StorageUserBox.Text.Trim());
        _dirty=false;SaveButton.IsEnabled=false;ApplyButton.IsEnabled=false;SettingsSaved?.Invoke(_settings);
        if(providerChanged)MessageBox.Show(Window.GetWindow(this),"Провайдер хранения изменён. Перезапустите приложение, чтобы изменения вступили в силу.","Хранилище",MessageBoxButton.OK,MessageBoxImage.Information);
        return true;
    }

    private void StorageProvider_Changed(object sender,RoutedEventArgs e)
    {
        if(PostgresConfigPanel is null)return;
        PostgresConfigPanel.Visibility=StorageProviderPostgresRadio.IsChecked==true?Visibility.Visible:Visibility.Collapsed;
        MarkDirty();
    }

    private void TestConnection_Click(object sender,RoutedEventArgs e)
    {
        var config=new StorageProviderConfig{Provider=StorageProviderKind.Postgres,PostgresConnectionString=PostgresConnectionStringBox.Text.Trim()};
        if(!PostgresConnectionString.TryValidate(config.PostgresConnectionString,out var invalid)){StorageProviderStatusText.Text=invalid;return;}
        StorageProviderStatusText.Text="Проверка соединения…";
        var ok=StorageConfigService.TestConnection(config,out var error,out var databaseMissing,out var failure);
        StorageProviderStatusText.Text=ok?(databaseMissing?"Сервер доступен. База ещё не создана — приложение создаст её и все таблицы само при переносе или запуске.":"Соединение с PostgreSQL успешно установлено."):"Не удалось подключиться: "+error;
        if(!ok&&failure is not null)MessageBox.Show(Window.GetWindow(this),PostgresErrorInfo.Detailed(failure),"Проверка соединения",MessageBoxButton.OK,MessageBoxImage.Error);
    }

    private async void MigrateToPostgres_Click(object sender,RoutedEventArgs e)
    {
        var connectionString=PostgresConnectionStringBox.Text.Trim();
        if(!PostgresConnectionString.TryValidate(connectionString,out var invalid)){StorageProviderStatusText.Text=invalid;return;}
        if(MessageBox.Show(Window.GetWindow(this),"Перенести текущую документацию (дерево, содержимое, теги, вложения, избранное, шаблоны) в указанную базу PostgreSQL? Сама база, таблицы и индексы будут созданы автоматически, если их ещё нет. Страницы, которые уже есть в базе, будут обновлены, а не продублированы. Удалённое в корзину и история версий не переносятся.","Перенос данных",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        var button=sender as Button;var previousContent=button?.Content;
        if(button is not null){button.IsEnabled=false;button.Content="Перенос…";}
        StorageProviderStatusText.Text="Начинается перенос…";
        try
        {
            var progress=new Progress<string>(text=>StorageProviderStatusText.Text=text);
            var report=await Task.Run(()=>
            {
                var source=new SqliteDocumentStore();source.Initialize();
                var target=new PostgresDocumentStore(connectionString);
                return StorageMigrationService.CopyAll(source,target,progress);
            });
            ShowReport(report,"Перенос данных в PostgreSQL");
            if(!report.HasFailures)StorageProviderStatusText.Text=report.Summary()+" Выберите PostgreSQL и сохраните настройки, чтобы приложение начало использовать новое хранилище.";
        }
        catch(Exception ex)
        {
            LogService.Error("Не удалось перенести данные в PostgreSQL",ex);
            StorageProviderStatusText.Text="Ошибка переноса: "+PostgresErrorInfo.Short(ex);
            MessageBox.Show(Window.GetWindow(this),PostgresErrorInfo.Detailed(ex),"Перенос данных в PostgreSQL",MessageBoxButton.OK,MessageBoxImage.Error);
        }
        finally
        {
            if(button is not null){button.IsEnabled=true;button.Content=previousContent;}
        }
    }

    /// <summary>
    /// Показывает итог переноса. Если часть изображений или вложений не доехала (обычно из-за
    /// прав пользователя базы), появляется кнопка повторного переноса только файлов — документацию
    /// заново копировать не нужно.
    /// </summary>
    private void ShowReport(MigrationReport report,string title)
    {
        StorageProviderStatusText.Text=report.Summary();
        _pendingAssets=report.FailedAssetNames.ToList();
        RetryAssetsButton.Visibility=_pendingAssets.Count>0?Visibility.Visible:Visibility.Collapsed;
        RetryAssetsButton.Content=$"Повторить перенос изображений ({_pendingAssets.Count})";
        LocalStateService.Set(PendingAssetsKey,string.Join('|',_pendingAssets));
        if(report.HasFailures)MessageBox.Show(Window.GetWindow(this),report.Details(),title,MessageBoxButton.OK,MessageBoxImage.Warning);
    }

    private const string PendingAssetsKey="PendingAssetTransfer";

    private void LoadPendingAssets()
    {
        _pendingAssets=(LocalStateService.Get(PendingAssetsKey)??"").Split('|',StringSplitOptions.RemoveEmptyEntries).ToList();
        RetryAssetsButton.Visibility=_pendingAssets.Count>0?Visibility.Visible:Visibility.Collapsed;
        if(_pendingAssets.Count>0)RetryAssetsButton.Content=$"Повторить перенос изображений ({_pendingAssets.Count})";
    }

    private async void RetryAssets_Click(object sender,RoutedEventArgs e)
    {
        var connectionString=PostgresConnectionStringBox.Text.Trim();
        if(!PostgresConnectionString.TryValidate(connectionString,out var invalid)){StorageProviderStatusText.Text=invalid;return;}
        if(_pendingAssets.Count==0){RetryAssetsButton.Visibility=Visibility.Collapsed;return;}
        var button=sender as Button;var previousContent=button?.Content;
        if(button is not null){button.IsEnabled=false;button.Content="Перенос файлов…";}
        try
        {
            var names=_pendingAssets.ToList();
            var progress=new Progress<string>(text=>StorageProviderStatusText.Text=text);
            var report=await Task.Run(()=>
            {
                var source=new SqliteDocumentStore();source.Initialize();
                var target=new PostgresDocumentStore(connectionString);
                return StorageMigrationService.CopyAssets(source,target,names,progress);
            });
            ShowReport(report,"Повторный перенос изображений");
        }
        catch(Exception ex)
        {
            LogService.Error("Не удалось повторно перенести изображения в PostgreSQL",ex);
            StorageProviderStatusText.Text="Ошибка переноса файлов: "+PostgresErrorInfo.Short(ex);
            MessageBox.Show(Window.GetWindow(this),PostgresErrorInfo.Detailed(ex),"Повторный перенос изображений",MessageBoxButton.OK,MessageBoxImage.Error);
        }
        finally
        {
            if(button is not null){button.IsEnabled=true;button.Content=previousContent;}
        }
    }

    private void Save_Click(object sender,RoutedEventArgs e){if(SaveSettings())CloseRequested?.Invoke();}
    /// <summary>Сохраняет настройки, не закрывая вкладку: результат виден сразу, правку можно продолжить.</summary>
    private void Apply_Click(object sender,RoutedEventArgs e){SaveSettings();}
    private void Cancel_Click(object sender,RoutedEventArgs e){if(RequestClose())CloseRequested?.Invoke();}
    public bool RequestClose()
    {
        if(!_dirty)return true;var answer=MessageBox.Show(Window.GetWindow(this),"Сохранить изменения настроек?","Несохранённые изменения",MessageBoxButton.YesNoCancel,MessageBoxImage.Question);if(answer==MessageBoxResult.Cancel)return false;if(answer==MessageBoxResult.Yes)return SaveSettings();LocalizationService.SetLanguage(_initialLanguage);return true;
    }
    private void Reset_Click(object sender,RoutedEventArgs e){if(MessageBox.Show(Window.GetWindow(this),"Вернуть настройки по умолчанию?","Сброс",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;_settings=new ApplicationSettings();_loading=true;LoadValues();_loading=false;LocalizationService.SetLanguage(_settings.Language);MarkDirty();}

    private static void OpenFolder(string path){Directory.CreateDirectory(path);Process.Start(new ProcessStartInfo("explorer.exe",$"\"{path}\""){UseShellExecute=true});}
    private void OpenData_Click(object sender,RoutedEventArgs e)=>OpenFolder(AppPaths.Data);
    private async void ChangeDataPath_Click(object sender,RoutedEventArgs e)
    {
        var owner=Window.GetWindow(this);var dialog=new OpenFolderDialog{Title=LocalizationService.T("Выберите папку, внутри которой будет создано хранилище MasterDocumentationData")};if(dialog.ShowDialog(owner)!=true)return;var source=Path.GetFullPath(AppPaths.Data);var target=Path.GetFullPath(Path.Combine(dialog.FolderName,"MasterDocumentationData"));
        if(target.Equals(source,StringComparison.OrdinalIgnoreCase)){MessageBox.Show(owner,"Это уже текущая папка данных.","Хранилище");return;}var prefix=source.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(target.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)){MessageBox.Show(owner,"Новое хранилище нельзя размещать внутри текущей папки данных.","Хранилище",MessageBoxButton.OK,MessageBoxImage.Warning);return;}
        if(Directory.Exists(target)&&Directory.EnumerateFileSystemEntries(target).Any()){MessageBox.Show(owner,$"Папка уже существует и не пуста:\n{target}\n\nВыберите другое расположение.","Хранилище",MessageBoxButton.OK,MessageBoxImage.Warning);return;}var required=StorageSize();var drive=new DriveInfo(Path.GetPathRoot(target)!);if(drive.AvailableFreeSpace<required+50L*1024*1024){MessageBox.Show(owner,"В выбранном расположении недостаточно свободного места.","Хранилище",MessageBoxButton.OK,MessageBoxImage.Warning);return;}
        if(MessageBox.Show(owner,$"Скопировать данные в новое хранилище?\n\n{target}\n\nПосле проверки приложение перезапустится. Старые данные останутся в {source}.","Перенос данных",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        var temporary=target+".migration-"+Guid.NewGuid().ToString("N");StorageStatsText.Text="Перенос данных… Не закрывайте приложение.";Mouse.OverrideCursor=Cursors.Wait;
        try
        {
            _database.Checkpoint();await Task.Run(()=>{CopyDataDirectory(source,temporary);var copiedDatabase=Path.Combine(temporary,Path.GetFileName(AppPaths.Database));if(!File.Exists(copiedDatabase))throw new InvalidDataException("В копии отсутствует база данных.");using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=copiedDatabase,Mode=SqliteOpenMode.ReadWrite,Pooling=false}.ToString());connection.Open();using var command=connection.CreateCommand();command.CommandText="PRAGMA integrity_check";var result=Convert.ToString(command.ExecuteScalar());if(!string.Equals(result,"ok",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Проверка SQLite не пройдена: "+result);if(Directory.Exists(target))Directory.Delete(target);Directory.Move(temporary,target);});AppPaths.SaveDataLocation(target);MessageBox.Show(owner,"Данные скопированы и проверены. Приложение сейчас закроется; при следующем запуске будет использовано новое хранилище.","Перенос завершён",MessageBoxButton.OK,MessageBoxImage.Information);Application.Current.Shutdown();
        }
        catch(Exception ex){try{if(Directory.Exists(temporary))Directory.Delete(temporary,true);}catch{}LoadStatistics();MessageBox.Show(owner,"Перенос отменён, текущее хранилище не изменено.\n\n"+ex.Message,"Ошибка переноса",MessageBoxButton.OK,MessageBoxImage.Error);}
        finally{Mouse.OverrideCursor=null;}
    }
    private void OpenExports_Click(object sender,RoutedEventArgs e)=>OpenFolder(AppPaths.Exports);
    private void OpenDocuments_Click(object sender,RoutedEventArgs e)=>OpenFolder(AppPaths.Documents);
    private void DatabaseTransfer_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new DatabaseTransferWindow(_database){Owner=Window.GetWindow(this)};
        dialog.ShowDialog();
        LoadStatistics();
    }
    private void OpenLogs_Click(object sender,RoutedEventArgs e)=>OpenFolder(AppPaths.Logs);
    private void CheckDatabase_Click(object sender,RoutedEventArgs e){try{var result=_database.CheckIntegrity();MessageBox.Show(Window.GetWindow(this),result=="ok"?"Целостность базы подтверждена.":"SQLite: "+result,"Проверка базы");}catch(Exception ex){MessageBox.Show(Window.GetWindow(this),ex.Message,"Ошибка",MessageBoxButton.OK,MessageBoxImage.Error);}}
    private void RebuildIndex_Click(object sender,RoutedEventArgs e){try{_database.RebuildSearchIndex();MessageBox.Show(Window.GetWindow(this),"Поисковый индекс перестроен.","Хранилище");}catch(Exception ex){MessageBox.Show(Window.GetWindow(this),"Не удалось перестроить индекс: "+ex.Message,"Ошибка",MessageBoxButton.OK,MessageBoxImage.Error);}}
    private void CleanupAssets_Click(object sender,RoutedEventArgs e){try{_database.CleanupUnusedAssets();LoadStatistics();MessageBox.Show(Window.GetWindow(this),"Неиспользуемые файлы удалены. Файлы, на которые ссылаются документы или версии, сохранены.","Хранилище");}catch(Exception ex){MessageBox.Show(Window.GetWindow(this),"Не удалось очистить файлы: "+ex.Message,"Ошибка",MessageBoxButton.OK,MessageBoxImage.Error);}}
    private async void QuickBackup_Click(object sender,RoutedEventArgs e)
    {
        string? password=null;
        if(EncryptionCheck.IsChecked==true)
        {
            var prompt=new PasswordDialog("Пароль для новой резервной копии"){Owner=Window.GetWindow(this)};
            if(prompt.ShowDialog()!=true)return;
            password=prompt.Value;
        }
        var button=sender as Button;
        var previousContent=button?.Content;
        if(button is not null)
        {
            button.Content="Создание копии…";
            button.IsEnabled=false;
            InteractionState.SetStatus(button,InteractionStatus.Loading);
        }
        try
        {
            var path=await Task.Run(()=>_backups.CreateBackup(false,password));
            LoadStatistics();
            if(button is not null)InteractionState.SetStatus(button,InteractionStatus.Success);
            ToastService.Show(
                "Резервная копия создана",
                path,
                ToastKind.Success,
                TimeSpan.FromSeconds(15),
                "Показать в папке",
                ()=>Process.Start(new ProcessStartInfo("explorer.exe",$"/select,\"{path}\""){UseShellExecute=true}));
        }
        catch(Exception ex)
        {
            LogService.Error("Не удалось создать резервную копию",ex);
            if(button is not null)InteractionState.SetStatus(button,InteractionStatus.Error);
            ToastService.Show(
                "Резервная копия не создана",
                "Проверьте свободное место и права записи. Подробности сохранены в журнале.",
                ToastKind.Error,
                TimeSpan.FromSeconds(25),
                "Подробнее",
                ()=>Dispatcher.BeginInvoke(new Action(()=>MessageBox.Show(Window.GetWindow(this),ex.Message,"Ошибка резервного копирования",MessageBoxButton.OK,MessageBoxImage.Error))));
        }
        finally
        {
            if(button is not null)
            {
                button.Content=previousContent;
                button.IsEnabled=true;
                var completedButton=button;
                _=Dispatcher.InvokeAsync(async()=>{
                    await Task.Delay(1400);
                    InteractionState.SetStatus(completedButton,InteractionStatus.Idle);
                });
            }
        }
    }

    private void LoadHotkeys(){var definitions=new[]{("NewDocument","Новый документ","Документы"),("NewFolder","Новая папка","Документы"),("Save","Сохранить","Документы"),("Export","Экспортировать","Документы"),("Settings","Настройки","Интерфейс"),("CloseTab","Закрыть вкладку","Вкладки"),("NextTab","Следующая вкладка","Вкладки"),("PreviousTab","Предыдущая вкладка","Вкладки")};_hotkeys=definitions.Select(x=>new HotkeySetting{Id=x.Item1,Command=x.Item2,Category=x.Item3,Gesture=_settings.Hotkeys.GetValueOrDefault(x.Item1,"")}).ToList();HotkeysGrid.ItemsSource=_hotkeys;}
    private bool ValidateHotkeys(){var converter=new KeyGestureConverter();foreach(var row in _hotkeys){try{if(string.IsNullOrWhiteSpace(row.Gesture)||converter.ConvertFromInvariantString(row.Gesture) is not KeyGesture)throw new FormatException();}catch{MessageBox.Show(Window.GetWindow(this),$"Некорректное сочетание для «{row.Command}»: {row.Gesture}","Горячие клавиши");return false;}}var conflict=_hotkeys.GroupBy(x=>x.Gesture,StringComparer.OrdinalIgnoreCase).FirstOrDefault(x=>x.Count()>1);if(conflict is not null){MessageBox.Show(Window.GetWindow(this),$"Сочетание {conflict.Key} назначено нескольким командам.","Конфликт");return false;}return true;}
    private void HotkeysGrid_CellEditEnding(object sender,DataGridCellEditEndingEventArgs e)=>MarkDirty();
    private void ResetHotkeys_Click(object sender,RoutedEventArgs e){_settings.Hotkeys=new ApplicationSettings().Hotkeys;LoadHotkeys();MarkDirty();}
    private void SettingsView_SizeChanged(object sender,SizeChangedEventArgs e)
    {
        var width=e.NewSize.Width;
        var showAside=width>=1080;
        SettingsAside.Visibility=showAside?Visibility.Visible:Visibility.Collapsed;
        SettingsAsideColumn.Width=showAside?new GridLength(285):new GridLength(0);
        SettingsAsideGapColumn.Width=showAside?new GridLength(8):new GridLength(0);
        SettingsNavigationColumn.Width=new GridLength(width<760?190:width<960?208:225);
    }
    private static long DirectorySize(string path){if(!Directory.Exists(path))return 0;try{return Directory.EnumerateFiles(path,"*",SearchOption.AllDirectories).Sum(x=>{try{return new FileInfo(x).Length;}catch{return 0;}});}catch{return 0;}}
    /// <summary>Размер хранилища считается по его содержимому: рядом могут лежать сам EXE и папка Runtime.</summary>
    private static long StorageSize(){return AppPaths.DataEntries.Sum(entry=>Directory.Exists(entry)?DirectorySize(entry):File.Exists(entry)?SafeLength(entry):0);}
    private static long SafeLength(string path){try{return new FileInfo(path).Length;}catch{return 0;}}
    /// <summary>
    /// Копируется только содержимое хранилища из явного списка: папка данных по умолчанию
    /// совпадает с папкой приложения, и копировать её целиком нельзя.
    /// </summary>
    private static void CopyDataDirectory(string source,string target)
    {
        Directory.CreateDirectory(target);
        foreach(var entry in AppPaths.DataEntries)
        {
            var relative=Path.GetRelativePath(source,entry);
            if(relative.StartsWith("..",StringComparison.Ordinal))continue;
            if(Directory.Exists(entry))CopyTree(entry,Path.Combine(target,relative));
            else if(File.Exists(entry))CopyFile(entry,Path.Combine(target,relative));
        }
    }
    private static void CopyTree(string source,string target)
    {
        Directory.CreateDirectory(target);
        foreach(var directory in Directory.EnumerateDirectories(source,"*",SearchOption.AllDirectories))
        {
            var relative=Path.GetRelativePath(source,directory);
            if(relative.StartsWith("WebView2",StringComparison.OrdinalIgnoreCase))continue;
            Directory.CreateDirectory(Path.Combine(target,relative));
        }
        foreach(var file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories))
        {
            var relative=Path.GetRelativePath(source,file);
            if(relative.StartsWith("WebView2",StringComparison.OrdinalIgnoreCase))continue;
            CopyFile(file,Path.Combine(target,relative));
        }
    }
    private static void CopyFile(string source,string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        using var output=new FileStream(destination,FileMode.Create,FileAccess.Write,FileShare.None);
        input.CopyTo(output);
    }
    private static string FormatBytes(long value){string[] units=["Б","КБ","МБ","ГБ","ТБ"];var size=(double)value;var i=0;while(size>=1024&&i<units.Length-1){size/=1024;i++;}return $"{size:0.##} {units[i]}";}
}

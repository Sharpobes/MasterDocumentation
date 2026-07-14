using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using MasterDocumentation.Models;
using MasterDocumentation.Services;
using MasterDocumentation.Storage;
using MasterDocumentation.Utilities;

namespace MasterDocumentation.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService = new(); private readonly DatabaseService _database; private readonly BackupService _backups;
    private ApplicationSettings _settings; private bool _loading = true; private bool _dirty; private bool _allowClose;
    private List<HotkeySetting> _hotkeys=[];
    private readonly FrameworkElement[] _panels;
    public ApplicationSettings SavedSettings => _settings;
    public SettingsWindow(DatabaseService database, BackupService backups)
    {
        InitializeComponent(); _database = database; _backups = backups; _settings = _settingsService.Load();
        _panels = [GeneralPanel, StoragePanel, EditorPanel, InterfacePanel, ExportPanel, HotkeysPanel, SecurityPanel, AboutPanel];
        DefaultFontBox.ItemsSource = Fonts.SystemFontFamilies.OrderBy(x => x.Source); LoadValues(); LoadStatistics(); _loading = false;
    }
    private void LoadValues()
    {
        SelectCombo(LanguageBox, _settings.Language); SelectCombo(StartupBox, _settings.StartupBehavior); RecentCountText.Text = _settings.RecentFilesCount.ToString(); CompactCheck.IsChecked = _settings.CompactMode;
        UpdatesCheck.IsChecked = _settings.CheckUpdates; TooltipsCheck.IsChecked = _settings.ShowTooltips; ConfirmDeleteCheck.IsChecked = _settings.ConfirmDelete; SelectCombo(LinkBox, _settings.LinkBehavior); SelectCombo(UnitsBox, _settings.MeasurementUnits);
        DefaultFontBox.SelectedItem = Fonts.SystemFontFamilies.FirstOrDefault(x => x.Source == _settings.DefaultFont); DefaultFontSizeBox.Text = _settings.DefaultFontSize.ToString(); SpellCheckBox.IsChecked = _settings.SpellCheck; AutoSaveDelayBox.Text = _settings.AutoSaveDelaySeconds.ToString(); SelectCombo(ThemeBox, _settings.Theme);
        EncryptionCheck.IsChecked = _settings.EncryptManualBackups;
        DataPathBox.Text = AppPaths.Data; AssetsPathText.Text = AppPaths.Assets; BackupsPathText.Text = AppPaths.Backups; ExportsPathText.Text = AppPaths.Exports; RuntimeText.Text = $"Версия .NET: {Environment.Version}"; AppPathText.Text = "Путь приложения: " + AppContext.BaseDirectory;
        LoadHotkeys();
        _dirty = false; SaveButton.IsEnabled = false;
    }
    private void LoadStatistics()
    {
        var dataBytes = DirectorySize(AppPaths.Data); var dbBytes = File.Exists(AppPaths.Database) ? new FileInfo(AppPaths.Database).Length : 0; var assetsBytes = DirectorySize(AppPaths.Assets); var attachments = Directory.Exists(AppPaths.Assets) ? Directory.EnumerateFiles(AppPaths.Assets, "*", SearchOption.AllDirectories).LongCount() : 0;
        var root = Path.GetPathRoot(AppPaths.Data)!; var drive = new DriveInfo(root); var usedPercent = drive.TotalSize == 0 ? 0 : (double)(drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize * 100;
        StoragePathText.Text = AppPaths.Data; StorageStatsText.Text = $"Данные: {FormatBytes(dataBytes)}\nБаза: {FormatBytes(dbBytes)}\nВложения: {FormatBytes(assetsBytes)} ({attachments})\nДокументы: {_database.CountDocuments()}\nДиск занят на {usedPercent:F0}%"; DiskUsageBar.Value = usedPercent;
        var last = Directory.Exists(AppPaths.Backups) ? Directory.EnumerateFiles(AppPaths.Backups).Select(x => new FileInfo(x)).OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault() : null;
        LastBackupText.Text = last is null ? "Резервных копий ещё нет" : $"Последняя копия: {last.LastWriteTime:g}, {FormatBytes(last.Length)}";
    }
    private static void SelectCombo(ComboBox box, string value) { box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Content?.ToString() == value); }
    private static string Selected(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? box.Text;
    private void Categories_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_panels is null) return; for (var i = 0; i < _panels.Length; i++) _panels[i].Visibility = i == Categories.SelectedIndex ? Visibility.Visible : Visibility.Collapsed; }
    private void Control_Changed(object sender, RoutedEventArgs e) => MarkDirty();
    private void Control_Changed(object sender, TextChangedEventArgs e) => MarkDirty();
    private void Control_Changed(object sender, SelectionChangedEventArgs e) => MarkDirty();
    private void MarkDirty() { if (_loading) return; _dirty = true; SaveButton.IsEnabled = true; }
    private void RecentMinus_Click(object sender, RoutedEventArgs e) { var n = Math.Max(1, int.Parse(RecentCountText.Text) - 1); RecentCountText.Text = n.ToString(); MarkDirty(); }
    private void RecentPlus_Click(object sender, RoutedEventArgs e) { var n = Math.Min(100, int.Parse(RecentCountText.Text) + 1); RecentCountText.Text = n.ToString(); MarkDirty(); }
    private bool SaveSettings()
    {
        if (!double.TryParse(DefaultFontSizeBox.Text, out var fontSize) || fontSize is < 6 or > 200) { MessageBox.Show(this, "Размер шрифта должен быть от 6 до 200.", "Настройки"); return false; }
        if (!int.TryParse(AutoSaveDelayBox.Text, out var delay) || delay is < 1 or > 3600) { MessageBox.Show(this, "Задержка автосохранения должна быть от 1 до 3600 секунд.", "Настройки"); return false; }
        HotkeysGrid.CommitEdit(DataGridEditingUnit.Cell,true);HotkeysGrid.CommitEdit(DataGridEditingUnit.Row,true);if(!ValidateHotkeys())return false;
        _settings.Language = Selected(LanguageBox); _settings.StartupBehavior = Selected(StartupBox); _settings.RecentFilesCount = int.Parse(RecentCountText.Text); _settings.CompactMode = CompactCheck.IsChecked == true; _settings.CheckUpdates = UpdatesCheck.IsChecked == true; _settings.ShowTooltips = TooltipsCheck.IsChecked == true; _settings.ConfirmDelete = ConfirmDeleteCheck.IsChecked == true; _settings.LinkBehavior = Selected(LinkBox); _settings.MeasurementUnits = Selected(UnitsBox); _settings.DefaultFont = (DefaultFontBox.SelectedItem as FontFamily)?.Source ?? DefaultFontBox.Text; _settings.DefaultFontSize = fontSize; _settings.SpellCheck = SpellCheckBox.IsChecked == true; _settings.AutoSaveDelaySeconds = delay; _settings.Theme = Selected(ThemeBox); _settings.EncryptManualBackups=EncryptionCheck.IsChecked==true;_settings.Hotkeys=_hotkeys.ToDictionary(x=>x.Id,x=>x.Gesture);
        _settingsService.Save(_settings); _dirty = false; SaveButton.IsEnabled = false; return true;
    }
    private void Save_Click(object sender, RoutedEventArgs e) { if (!SaveSettings()) return; _allowClose = true; DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { _allowClose = true; DialogResult = false; }
    private void Reset_Click(object sender, RoutedEventArgs e) { if (MessageBox.Show(this, "Вернуть все настройки к значениям по умолчанию?", "Сброс настроек", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; _settings = new ApplicationSettings(); _loading = true; LoadValues(); _loading = false; MarkDirty(); }
    private void Window_Closing(object? sender, CancelEventArgs e) { if (_allowClose || !_dirty) return; var answer = MessageBox.Show(this, "Сохранить изменения настроек?", "Несохранённые изменения", MessageBoxButton.YesNoCancel, MessageBoxImage.Question); if (answer == MessageBoxResult.Cancel) { e.Cancel = true; return; } if (answer == MessageBoxResult.Yes && !SaveSettings()) { e.Cancel = true; return; } _allowClose = true; }
    private static void OpenFolder(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
    private void OpenData_Click(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.Data);
    private void OpenExports_Click(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.Exports);
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.Logs);
    private void QuickBackup_Click(object sender, RoutedEventArgs e) { try { string? password=null;if(EncryptionCheck.IsChecked==true){var prompt=new PasswordDialog("Пароль для новой резервной копии"){Owner=this};if(prompt.ShowDialog()!=true)return;password=prompt.Value;}var path = _backups.CreateBackup(false,password); LoadStatistics(); MessageBox.Show(this, "Резервная копия создана:\n" + path, "Готово"); } catch (Exception ex) { MessageBox.Show(this, "Не удалось создать резервную копию: " + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); } }
    private void CheckDatabase_Click(object sender, RoutedEventArgs e) { try { var result = _database.CheckIntegrity(); MessageBox.Show(this, result == "ok" ? "Целостность базы данных подтверждена." : "SQLite: " + result, "Проверка базы"); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); } }
    private static long DirectorySize(string path) { if (!Directory.Exists(path)) return 0; try { return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(x => { try { return new FileInfo(x).Length; } catch { return 0; } }); } catch { return 0; } }
    private static string FormatBytes(long value) { string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"]; var size = (double)value; var i = 0; while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; } return $"{size:0.##} {units[i]}"; }
    private void LoadHotkeys(){var definitions=new[]{("NewDocument","Новый документ","Документы"),("NewFolder","Новая папка","Документы"),("Save","Сохранить","Документы"),("Export","Экспортировать","Документы"),("Settings","Настройки","Интерфейс"),("CloseTab","Закрыть вкладку","Вкладки"),("NextTab","Следующая вкладка","Вкладки"),("PreviousTab","Предыдущая вкладка","Вкладки")};_hotkeys=definitions.Select(x=>new HotkeySetting{Id=x.Item1,Command=x.Item2,Category=x.Item3,Gesture=_settings.Hotkeys.GetValueOrDefault(x.Item1,"")}).ToList();HotkeysGrid.ItemsSource=_hotkeys;}
    private bool ValidateHotkeys(){var converter=new KeyGestureConverter();foreach(var row in _hotkeys){try{if(string.IsNullOrWhiteSpace(row.Gesture)||converter.ConvertFromInvariantString(row.Gesture) is not KeyGesture)throw new FormatException();}catch{MessageBox.Show(this,$"Некорректное сочетание для команды «{row.Command}»: {row.Gesture}","Горячие клавиши");return false;}}var conflict=_hotkeys.GroupBy(x=>x.Gesture,StringComparer.OrdinalIgnoreCase).FirstOrDefault(x=>x.Count()>1);if(conflict is not null){MessageBox.Show(this,$"Сочетание {conflict.Key} назначено нескольким командам.","Конфликт горячих клавиш");return false;}return true;}
    private void HotkeysGrid_CellEditEnding(object sender,DataGridCellEditEndingEventArgs e)=>MarkDirty();
    private void ResetHotkeys_Click(object sender,RoutedEventArgs e){_settings.Hotkeys=new ApplicationSettings().Hotkeys;LoadHotkeys();MarkDirty();}
}

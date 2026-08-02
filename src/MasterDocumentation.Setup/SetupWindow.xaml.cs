using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace MasterDocumentation.Setup;

public partial class SetupWindow : Window
{
    private readonly CancellationTokenSource _cancellation = new();
    private bool _running;
    private bool _finished;
    private string _installedDirectory = "";

    public SetupWindow()
    {
        InitializeComponent();
        SubtitleText.Text = $"Версия {InstallEngine.Version} · локальная документация без облака и регистрации";
        TargetBox.Text = InstallEngine.DefaultInstallDirectory;
        UpdateSpaceHint();
        StatusText.Text = PayloadArchive.Exists
            ? "Права администратора не требуются."
            : "Внимание: в этом файле нет дистрибутива приложения.";
        PrimaryButton.IsEnabled = PayloadArchive.Exists;
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (TargetBox is null) return;
        var portable = PortableModeRadio.IsChecked == true;
        var previousDefault = portable ? InstallEngine.DefaultInstallDirectory : InstallEngine.DefaultPortableDirectory;
        if (string.IsNullOrWhiteSpace(TargetBox.Text) || string.Equals(TargetBox.Text.Trim(), previousDefault, StringComparison.OrdinalIgnoreCase))
            TargetBox.Text = portable ? InstallEngine.DefaultPortableDirectory : InstallEngine.DefaultInstallDirectory;
        StartMenuShortcutCheck.IsEnabled = !portable;
        PrimaryButton.Content = portable ? "Распаковать" : "Установить";
        UpdateSpaceHint();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите папку", InitialDirectory = SafeDirectory(TargetBox.Text) };
        if (dialog.ShowDialog(this) != true) return;
        var chosen = dialog.FolderName;
        // Папку с приложением создаём внутри выбранной, если выбран не она сама.
        if (!Path.GetFileName(chosen.TrimEnd(Path.DirectorySeparatorChar)).Equals(InstallEngine.ApplicationName, StringComparison.OrdinalIgnoreCase))
            chosen = Path.Combine(chosen, InstallEngine.ApplicationName);
        TargetBox.Text = chosen;
        UpdateSpaceHint();
    }

    private static string SafeDirectory(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            while (full.Length > 3 && !Directory.Exists(full))
                full = Path.GetDirectoryName(full) ?? "";
            return Directory.Exists(full) ? full : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        catch { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
    }

    private void UpdateSpaceHint()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(TargetBox.Text));
            if (string.IsNullOrEmpty(root)) { SpaceText.Text = ""; return; }
            var drive = new DriveInfo(root);
            SpaceText.Text = $"Требуется около 700 МБ. Свободно на диске {drive.Name.TrimEnd('\\')}: {drive.AvailableFreeSpace / 1024d / 1024 / 1024:0.0} ГБ.";
        }
        catch { SpaceText.Text = "Требуется около 700 МБ свободного места."; }
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_finished) { Close(); return; }
        if (_running) return;

        var options = new InstallOptions
        {
            Mode = PortableModeRadio.IsChecked == true ? InstallMode.Portable : InstallMode.Install,
            TargetDirectory = TargetBox.Text.Trim(),
            DesktopShortcut = DesktopShortcutCheck.IsChecked == true,
            StartMenuShortcut = StartMenuShortcutCheck.IsChecked == true && StartMenuShortcutCheck.IsEnabled,
            LaunchAfterSetup = LaunchCheck.IsChecked == true,
        };
        if (options.TargetDirectory.Length == 0)
        {
            MessageBox.Show(this, "Укажите папку установки.", "Установка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _running = true;
        OptionsPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        PrimaryButton.IsEnabled = false;
        StatusText.Text = "Не закрывайте окно до конца установки.";

        var progress = new Progress<InstallProgress>(value =>
        {
            ProgressText.Text = value.Text;
            ProgressBarControl.Value = value.Value;
        });

        try
        {
            await Task.Run(() => InstallEngine.Run(options, progress, _cancellation.Token), _cancellation.Token);
            _installedDirectory = Path.GetFullPath(options.TargetDirectory);
            ShowResult(options);
            if (options.LaunchAfterSetup) LaunchApplication();
        }
        catch (OperationCanceledException)
        {
            Close();
        }
        catch (Exception ex)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            OptionsPanel.Visibility = Visibility.Visible;
            PrimaryButton.IsEnabled = true;
            StatusText.Text = "Установка не выполнена.";
            MessageBox.Show(this, ex.Message, "Установка не выполнена", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _running = false;
        }
    }

    private void ShowResult(InstallOptions options)
    {
        _finished = true;
        ProgressPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;
        DoneTitle.Text = options.Mode == InstallMode.Portable ? "Портативная версия готова" : "Установка завершена";
        DoneText.Text = options.Mode == InstallMode.Portable
            ? $"Приложение распаковано в {_installedDirectory}.\nДокументация хранится в подпапке Data — переносите папку целиком.\nЗапуск: MasterDocumentation.exe."
            : $"Приложение установлено в {_installedDirectory}.\nДокументация хранится в {InstallEngine.DefaultDataDirectory}.\nУдаление — через «Параметры → Приложения» или ярлык «Удалить».";
        PrimaryButton.Content = "Готово";
        PrimaryButton.IsEnabled = true;
        CancelButton.Visibility = Visibility.Collapsed;
        StatusText.Text = "";
    }

    private void LaunchApplication()
    {
        try
        {
            var executable = Path.Combine(_installedDirectory, InstallEngine.ExecutableName);
            if (File.Exists(executable))
                Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = _installedDirectory });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось запустить приложение: " + ex.Message, "Установка", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_installedDirectory)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_installedDirectory}\"") { UseShellExecute = true });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_running && MessageBox.Show(this, "Прервать установку?", "Установка", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _cancellation.Cancel();
        Close();
    }
}

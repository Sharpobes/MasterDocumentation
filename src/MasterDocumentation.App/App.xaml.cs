using System.Windows;
using System.Windows.Threading;
using MasterDocumentation.Services;
using MasterDocumentation.Storage;
using MasterDocumentation.ViewModels;
using MasterDocumentation.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using MasterDocumentation.Utilities;
using MessageBox = MasterDocumentation.Views.MessageBox;

namespace MasterDocumentation;

public partial class App : Application
{
    private ServiceProvider? _services;
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogService.Error("Необработанная ошибка", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { LogService.Error("Необработанная ошибка фоновой задачи", args.Exception); args.SetObserved(); };
        base.OnStartup(e);
        // До появления главного окна режим OnMainWindowClose опасен: WPF назначает главным
        // первое созданное окно, поэтому закрытие мастера первого запуска завершало приложение
        // раньше, чем оно успевало открыться. Обычный режим возвращается после показа окна.
        ShutdownMode=ShutdownMode.OnExplicitShutdown;
        if(Uninstaller.IsRequested(e.Args)){Uninstaller.Run(e.Args);Shutdown();return;}
        AppPaths.MigrateLegacyDataFolder();
        if(!EnsureWritableStorage()){Shutdown();return;}
        string? firstDocumentTitle=null;
        var bootstrapSettingsService=new SettingsService();var bootstrapSettings=bootstrapSettingsService.Load();
        LocalizationService.Enable();
        var initialLanguage=bootstrapSettings.FirstRunCompleted
            ?bootstrapSettings.Language
            :System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru",StringComparison.OrdinalIgnoreCase)?LocalizationService.Russian:LocalizationService.English;
        LocalizationService.SetLanguage(initialLanguage);
        if(!bootstrapSettings.FirstRunCompleted)
        {
            var wizard=new FirstRunWizard();if(wizard.ShowDialog()!=true){Shutdown();return;}
            try
            {
                if(!Path.GetFullPath(wizard.SelectedDataPath).Equals(Path.GetFullPath(AppPaths.Data),StringComparison.OrdinalIgnoreCase))AppPaths.SaveDataLocation(wizard.SelectedDataPath,true);
                AppPaths.Ensure();bootstrapSettings.Language=wizard.SelectedLanguage;bootstrapSettings.Theme=wizard.SelectedTheme;bootstrapSettings.AutomaticBackups=wizard.AutomaticBackups;bootstrapSettings.FirstRunCompleted=true;bootstrapSettings.OpenDocumentIds=[];bootstrapSettings.SelectedDocumentId=null;new SettingsService().Save(bootstrapSettings);firstDocumentTitle=wizard.FirstDocumentTitle;
                LocalizationService.SetLanguage(wizard.SelectedLanguage);
            }
            catch(Exception ex){LogService.Error("Не удалось завершить первоначальную настройку",ex);MessageBox.Show("Не удалось подготовить выбранное хранилище:\n"+ex.Message,"Первый запуск",MessageBoxButton.OK,MessageBoxImage.Error);Shutdown();return;}
        }
        var services = new ServiceCollection(); services.AddSingleton<SettingsService>(); services.AddSingleton<DatabaseService>(); services.AddSingleton<BackupService>(); services.AddSingleton<MainViewModel>(); services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var viewModel=_services.GetRequiredService<MainViewModel>();
        if(!string.IsNullOrWhiteSpace(firstDocumentTitle))
        {
            var title=firstDocumentTitle.Trim();var candidate=title;for(var index=2;viewModel.Database.TitleExists(null,candidate);index++)candidate=$"{title} ({index})";var id=viewModel.Database.Create(null,false,candidate);var current=_services.GetRequiredService<SettingsService>().Load();current.OpenDocumentIds=[id];current.SelectedDocumentId=id;_services.GetRequiredService<SettingsService>().Save(current);viewModel.ReloadTree();
        }
        MainWindow = _services.GetRequiredService<MainWindow>(); MainWindow.Show(); ShutdownMode=ShutdownMode.OnMainWindowClose;
    }

    protected override void OnExit(ExitEventArgs e) { _services?.Dispose(); base.OnExit(e); }

    private static bool EnsureWritableStorage()
    {
        try{AppPaths.Ensure();var probe=Path.Combine(AppPaths.Temp,".write-test-"+Guid.NewGuid().ToString("N"));File.WriteAllText(probe,"ok");File.Delete(probe);return true;}
        catch(Exception first)
        {
            if(MessageBox.Show($"Папка данных недоступна для записи:\n{AppPaths.Data}\n\n{first.Message}\n\nВыбрать другое расположение?","Хранилище недоступно",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return false;
            var dialog=new OpenFolderDialog{Title=LocalizationService.T("Выберите папку для локальных данных MasterDocumentation")};if(dialog.ShowDialog()!=true)return false;
            try{var target=Path.Combine(dialog.FolderName,"MasterDocumentationData");AppPaths.SaveDataLocation(target,true);AppPaths.Ensure();var probe=Path.Combine(AppPaths.Temp,".write-test");File.WriteAllText(probe,"ok");File.Delete(probe);return true;}
            catch(Exception second){MessageBox.Show("Не удалось подготовить выбранную папку:\n"+second.Message,"Хранилище недоступно",MessageBoxButton.OK,MessageBoxImage.Error);return false;}
        }
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogService.Error("Ошибка интерфейса", e.Exception);
        MessageBox.Show($"Произошла ошибка: {e.Exception.Message}\nПодробности записаны в папку Logs.",
            "MasterDocumentation", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

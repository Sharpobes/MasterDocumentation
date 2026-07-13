using System.Windows;
using System.Windows.Threading;
using MasterDocumentation.Services;
using MasterDocumentation.Storage;
using MasterDocumentation.ViewModels;
using MasterDocumentation.Views;
using Microsoft.Extensions.DependencyInjection;

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
        var services = new ServiceCollection(); services.AddSingleton<SettingsService>(); services.AddSingleton<DatabaseService>(); services.AddSingleton<BackupService>(); services.AddSingleton<MainViewModel>(); services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }); MainWindow = _services.GetRequiredService<MainWindow>(); MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e) { _services?.Dispose(); base.OnExit(e); }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogService.Error("Ошибка интерфейса", e.Exception);
        MessageBox.Show($"Произошла ошибка: {e.Exception.Message}\nПодробности записаны в Data\\Logs.",
            "MasterDocumentation", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

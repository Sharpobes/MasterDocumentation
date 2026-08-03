using System.Windows;

namespace MasterDocumentation.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Установка MasterDocumentation", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
        // Обновление приложение запускает само и передаёт готовые параметры — окно выбора не нужно.
        MainWindow = new SetupWindow(UpdateRequest.Parse(e.Args));
        MainWindow.Show();
    }
}

using System.Windows;
using System.Windows.Controls;
using MasterDocumentation.Models;
using MasterDocumentation.Services;
using MasterDocumentation.Storage;
using MasterDocumentation.Utilities;
using MasterDocumentation.ViewModels;

namespace MasterDocumentation.Views;

/// <summary>
/// Обмен документацией с базой данных: выбор конкретных страниц локального хранилища для
/// записи в общую базу и обратный импорт выбранных страниц из базы в текущее хранилище.
/// Приватные страницы принадлежат своему создателю и не видны другим пользователям базы.
/// </summary>
public partial class DatabaseTransferWindow : Window
{
    private readonly DatabaseService _database;
    private StorageProviderConfig _config;
    private bool _busy;
    /// <summary>Файлы, не доехавшие при последнем переносе, и направление, в котором их нужно доложить.</summary>
    private List<string> _pendingAssets = [];
    private bool _pendingAssetsFromRemote;

    /// <summary>Истина, если в текущее хранилище были импортированы документы и дерево нужно перечитать.</summary>
    public bool LibraryChanged { get; private set; }

    public DatabaseTransferWindow(DatabaseService database)
    {
        InitializeComponent();
        _database = database;
        _config = StorageConfigService.Load();
        ConnectionBox.Text = _config.PostgresConnectionString;
        UserBox.Text = UserIdentity.Current;
        LoadLocalTree();
        StatusText.Text = _database.ActiveProvider == StorageProviderKind.Postgres
            ? "Текущее хранилище — PostgreSQL."
            : "Текущее хранилище — локальный файл SQLite.";
    }

    private string ConnectionString => ConnectionBox.Text.Trim();

    private void ApplyUserName()
    {
        var name = UserBox.Text.Trim();
        if (name.Length > 0 && name != UserIdentity.Current) UserIdentity.Set(name);
    }

    private void LoadLocalTree()
    {
        var roots = _database.LoadTree().Select(node => new SelectableNode(node, id => SafeAccess(_database, id))).ToList();
        LocalTree.ItemsSource = roots;
    }

    private static DocumentAccess? SafeAccess(IDocumentStore store, long id)
    {
        try { return store.GetDocumentAccess(id); }
        catch { return null; }
    }

    private IDocumentStore CreateRemoteStore()
    {
        PostgresConnectionString.Validate(ConnectionString);
        return new PostgresDocumentStore(ConnectionString);
    }

    private void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        ApplyUserName();
        var config = new StorageProviderConfig { Provider = StorageProviderKind.Postgres, PostgresConnectionString = ConnectionString };
        if (!PostgresConnectionString.TryValidate(config.PostgresConnectionString, out var invalid)) { StatusText.Text = invalid; return; }
        StatusText.Text = StorageConfigService.TestConnection(config, out var error, out var databaseMissing, out _)
            ? (databaseMissing ? "Сервер доступен, база ещё не создана — она и все таблицы будут созданы автоматически при первом переносе или запуске." : "Соединение установлено.")
            : "Не удалось подключиться: " + error;
    }

    private void SaveAsDefault_Click(object sender, RoutedEventArgs e)
    {
        ApplyUserName();
        if (ConnectionString.Length == 0) { StatusText.Text = "Укажите строку подключения."; return; }
        _config = new StorageProviderConfig { Provider = StorageProviderKind.Postgres, PostgresConnectionString = ConnectionString };
        StorageConfigService.Save(_config);
        StatusText.Text = "Настройки сохранены. Приложение начнёт работать с этой базой после перезапуска.";
    }

    private void ReloadLocal_Click(object sender, RoutedEventArgs e) => LoadLocalTree();
    private void SelectAllLocal_Click(object sender, RoutedEventArgs e) => SetAll(LocalTree, true);
    private void ClearLocal_Click(object sender, RoutedEventArgs e) => SetAll(LocalTree, false);
    private void SelectAllRemote_Click(object sender, RoutedEventArgs e) => SetAll(RemoteTree, true);
    private void ClearRemote_Click(object sender, RoutedEventArgs e) => SetAll(RemoteTree, false);

    private static void SetAll(TreeView tree, bool value)
    {
        if (tree.ItemsSource is not IEnumerable<SelectableNode> roots) return;
        foreach (var root in roots) root.SetChecked(value, true, false);
    }

    private static List<long> SelectedDocuments(TreeView tree)
    {
        if (tree.ItemsSource is not IEnumerable<SelectableNode> roots) return [];
        return roots.SelectMany(x => x.Flatten()).Where(x => !x.IsFolder && x.IsChecked == true).Select(x => x.Id).ToList();
    }

    private async void ReloadRemote_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ApplyUserName();
        await RunAsync("Чтение списка документов из базы данных…", () =>
        {
            var store = CreateRemoteStore();
            store.Initialize();
            var roots = store.LoadTree();
            var access = roots.SelectMany(Flatten).Where(x => !x.IsFolder).ToDictionary(x => x.Id, x => SafeAccess(store, x.Id));
            return (object)(roots, access);
        },
        result =>
        {
            var (roots, access) = ((List<NodeItem>, Dictionary<long, DocumentAccess?>))result!;
            RemoteTree.ItemsSource = roots.Select(node => new SelectableNode(node, id => access.GetValueOrDefault(id))).ToList();
            var count = roots.SelectMany(Flatten).Count(x => !x.IsFolder);
            StatusText.Text = count == 0 ? "В базе данных пока нет доступных вам документов." : $"Доступно документов в базе: {count}.";
        });
    }

    private static IEnumerable<NodeItem> Flatten(NodeItem node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var item in Flatten(child))
                yield return item;
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ApplyUserName();
        var ids = SelectedDocuments(LocalTree);
        if (ids.Count == 0) { StatusText.Text = "Отметьте хотя бы один документ для выгрузки."; return; }
        if (ConnectionString.Length == 0) { StatusText.Text = "Укажите строку подключения."; return; }
        var isPrivate = UploadPrivateCheck.IsChecked == true;
        if (MessageBox.Show(this, $"Сохранить выбранные документы ({ids.Count}) в базу данных?" + (isPrivate ? "\n\nСтраницы будут помечены приватными: их увидите только вы." : "\n\nСтраницы будут доступны всем, кто подключится к этой базе."), "Выгрузка в базу данных", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync("Выгрузка документов в базу данных…", () =>
        {
            var target = CreateRemoteStore();
            var progress = new Progress<string>(text => Dispatcher.Invoke(() => StatusText.Text = text));
            return StorageMigrationService.CopySelected(_database, target, ids, isPrivate ? true : null, progress);
        },
        result => ShowReport((MigrationReport)result!, "Выгрузка в базу данных"));
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ApplyUserName();
        var ids = SelectedDocuments(RemoteTree);
        if (ids.Count == 0) { StatusText.Text = "Отметьте хотя бы один документ для импорта."; return; }
        if (MessageBox.Show(this, $"Импортировать выбранные документы ({ids.Count}) в текущее хранилище?", "Импорт из базы данных", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync("Импорт документов из базы данных…", () =>
        {
            var source = CreateRemoteStore();
            source.Initialize();
            var progress = new Progress<string>(text => Dispatcher.Invoke(() => StatusText.Text = text));
            return StorageMigrationService.CopySelected(source, _database, ids, null, progress);
        },
        result =>
        {
            LibraryChanged = true;
            LoadLocalTree();
            ShowReport((MigrationReport)result!, "Импорт из базы данных");
        });
    }

    /// <summary>
    /// Итог переноса. Если часть изображений и вложений не доехала, показывается подробный
    /// отчёт с причиной и включается кнопка повторного переноса только файлов.
    /// </summary>
    private void ShowReport(MigrationReport report, string title)
    {
        StatusText.Text = report.Summary();
        _pendingAssets = report.FailedAssetNames.ToList();
        _pendingAssetsFromRemote = title.StartsWith("Импорт", StringComparison.Ordinal);
        RetryAssetsButton.Visibility = _pendingAssets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RetryAssetsButton.Content = $"Повторить перенос изображений ({_pendingAssets.Count})";
        if (report.HasFailures) MessageBox.Show(this, report.Details(), title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void RetryAssets_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _pendingAssets.Count == 0) return;
        ApplyUserName();
        var names = _pendingAssets.ToList();
        var fromRemote = _pendingAssetsFromRemote;
        await RunAsync("Повторный перенос изображений…", () =>
        {
            var remote = CreateRemoteStore();
            var progress = new Progress<string>(text => Dispatcher.Invoke(() => StatusText.Text = text));
            return fromRemote
                ? StorageMigrationService.CopyAssets(remote, _database, names, progress)
                : StorageMigrationService.CopyAssets(_database, remote, names, progress);
        },
        result => ShowReport((MigrationReport)result!, fromRemote ? "Импорт из базы данных" : "Выгрузка в базу данных"));
    }

    private async Task RunAsync(string title, Func<object?> operation, Action<object?> onSuccess)
    {
        _busy = true;
        UploadButton.IsEnabled = false;
        ImportButton.IsEnabled = false;
        RetryAssetsButton.IsEnabled = false;
        StatusText.Text = title;
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var result = await Task.Run(operation);
            onSuccess(result);
        }
        catch (Exception ex)
        {
            LogService.Error(title, ex);
            StatusText.Text = "Ошибка: " + PostgresErrorInfo.Short(ex);
            MessageBox.Show(this, PostgresErrorInfo.Detailed(ex), "База данных", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
            UploadButton.IsEnabled = true;
            ImportButton.IsEnabled = true;
            RetryAssetsButton.IsEnabled = true;
            _busy = false;
        }
    }
}

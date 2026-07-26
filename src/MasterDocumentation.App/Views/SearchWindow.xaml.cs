using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MasterDocumentation.Storage;
using MasterDocumentation.UI;

namespace MasterDocumentation.Views;

public sealed record SearchPaletteEntry(long? DocumentId, string? ActionId, string Title, string Snippet, string Meta);

public partial class SearchWindow : Window
{
    private static readonly SearchPaletteEntry[] Actions =
    [
        new(null, "NewDocument", "Создать документ", "Создать пустой документ в выбранном разделе.", "Команда  •  Ctrl+N"),
        new(null, "NewFolder", "Создать папку", "Добавить папку или раздел в структуру документации.", "Команда  •  Ctrl+Shift+N"),
        new(null, "Export", "Экспортировать документ", "Сохранить текущий документ как PDF, DOCX, HTML, Markdown или текст.", "Команда  •  Ctrl+Shift+S"),
        new(null, "Print", "Печать документа", "Открыть системный интерфейс печати текущего документа.", "Команда  •  Ctrl+P"),
        new(null, "Settings", "Открыть настройки", "Тема, редактор, хранилище, экспорт и горячие клавиши.", "Команда"),
        new(null, "ToggleTheme", "Переключить тему", "Переключить светлое и тёмное оформление.", "Команда"),
        new(null, "FocusMode", "Режим фокусировки", "Скрыть боковые панели и оставить редактор.", "Команда  •  Ctrl+Shift+F"),
        new(null, "Backup", "Создать резервную копию", "Сохранить защищённую локальную копию всего хранилища.", "Команда"),
        new(null, "Attachments", "Показать вложения", "Открыть правую панель файлов текущего документа.", "Команда")
    ];

    private readonly DatabaseService _database;
    private readonly DispatcherTimer _debounce;
    private int _searchGeneration;

    public long? SelectedDocumentId => (ResultsList.SelectedItem as SearchPaletteEntry)?.DocumentId;
    public string? SelectedActionId => (ResultsList.SelectedItem as SearchPaletteEntry)?.ActionId;

    public SearchWindow(DatabaseService database, string initialQuery = "")
    {
        InitializeComponent();
        _database = database;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Search();
        };
        QueryBox.Text = initialQuery;
        Loaded += (_, _) =>
        {
            QueryBox.Focus();
            QueryBox.SelectAll();
            Search();
        };
    }

    private static string Selected(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void Search_Click(object sender, RoutedEventArgs e) => Search();

    private async void Search()
    {
        var generation=++_searchGeneration;
        var query = QueryBox.Text.Trim();
        var status=Selected(StatusBox);
        var tag=TagBox.Text;
        var favorites=FavoritesBox.IsChecked==true;
        var attachments=AttachmentsBox.IsChecked==true;
        var trash=TrashBox.IsChecked==true;
        var byDate=Selected(SortBox)=="По дате";
        ResultsList.IsEnabled=false;
        EmptyState.Visibility=Visibility.Visible;
        EmptyTitle.Text="Поиск…";
        EmptyDescription.Text="Ищем по названиям, содержимому, тегам и командам.";
        RebuildIndexButton.Visibility=Visibility.Collapsed;
        SearchButton.IsEnabled=false;
        InteractionState.SetStatus(SearchButton,InteractionStatus.Loading);
        ResultCountText.Text="Выполняется поиск…";
        try
        {
            var normalized = query.ToLowerInvariant();
            var entries = new List<SearchPaletteEntry>();
            entries.AddRange(Actions.Where(action =>
                string.IsNullOrWhiteSpace(normalized) ||
                $"{action.Title} {action.Snippet} {action.Meta}".ToLowerInvariant().Contains(normalized)));

            var documents = await Task.Run(()=>_database.SearchDocuments(
                query,status,tag,favorites,attachments,trash,byDate));
            if(generation!=_searchGeneration)return;
            entries.AddRange(documents.Select(document => new SearchPaletteEntry(
                document.Id,
                null,
                document.Title,
                document.Snippet,
                $"{document.Status}  •  {document.ModifiedAt:dd.MM.yyyy HH:mm}")));

            ResultsList.ItemsSource = entries;
            ResultsList.SelectedIndex = entries.Count > 0 ? 0 : -1;
            EmptyState.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyTitle.Text="Ничего не найдено";
            EmptyDescription.Text="Измените запрос или снимите часть фильтров.";
            ResultCountText.Text = $"Найдено: {entries.Count}  •  Enter — открыть  •  Esc — закрыть";
        }
        catch (Exception ex)
        {
            if(generation!=_searchGeneration)return;
            ResultsList.ItemsSource = null;
            EmptyState.Visibility = Visibility.Visible;
            EmptyTitle.Text="Не удалось выполнить поиск";
            EmptyDescription.Text="Поисковый индекс может быть повреждён. Перестройте его и повторите запрос.";
            RebuildIndexButton.Visibility=Visibility.Visible;
            ResultCountText.Text = "Ошибка поиска. Подробности сохранены в журнале.";
            MasterDocumentation.Services.LogService.Error("Ошибка поиска",ex);
        }
        finally
        {
            if(generation==_searchGeneration)
            {
                ResultsList.IsEnabled=true;
                SearchButton.IsEnabled=true;
                InteractionState.SetStatus(SearchButton,InteractionStatus.Idle);
            }
        }
    }
    private async void RebuildIndex_Click(object sender,RoutedEventArgs e)
    {
        RebuildIndexButton.IsEnabled=false;
        InteractionState.SetStatus(RebuildIndexButton,InteractionStatus.Loading);
        try
        {
            await Task.Run(_database.RebuildSearchIndex);
            Search();
        }
        catch(Exception ex)
        {
            MasterDocumentation.Services.LogService.Error("Не удалось перестроить поисковый индекс",ex);
            EmptyDescription.Text="Индекс не перестроен. Проверьте доступ к хранилищу и откройте журнал приложения.";
            InteractionState.SetStatus(RebuildIndexButton,InteractionStatus.Error);
        }
        finally
        {
            RebuildIndexButton.IsEnabled=true;
            if(InteractionState.GetStatus(RebuildIndexButton)!=InteractionStatus.Error)
                InteractionState.SetStatus(RebuildIndexButton,InteractionStatus.Idle);
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is null)
        {
            MessageBox.Show(this, "Выберите документ или команду.", "Поиск", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void Results_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is not null) DialogResult = true;
    }
}

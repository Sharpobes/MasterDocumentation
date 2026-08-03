using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace MasterDocumentation.Views;

/// <summary>
/// Центр уведомлений: список всех показанных уведомлений с их действиями. Как и карточки,
/// это дочернее окно главного, а не окно поверх всех — иначе панель осталась бы на экране,
/// когда приложение свёрнуто. Отдельное окно нужно потому, что редактор на WebView2 рисуется
/// собственным HWND поверх элементов WPF внутри главного окна.
/// </summary>
public partial class NotificationCenterWindow : Window
{
    private readonly Window _owner;

    public NotificationCenterWindow(Window owner)
    {
        InitializeComponent();
        _owner = owner;
        Owner = owner;
        Items.ItemsSource = ToastService.History;
        ToastService.History.CollectionChanged += History_Changed;
        UpdateEmptyState();
        Deactivated += (_, _) => Close();
        Closed += (_, _) => ToastService.History.CollectionChanged -= History_Changed;
    }

    /// <summary>Открывает панель над указанной точкой экрана (кнопкой в строке состояния).</summary>
    public void ShowAbove(FrameworkElement anchor)
    {
        var source = PresentationSource.FromVisual(_owner)?.CompositionTarget;
        var topLeft = anchor.PointToScreen(new Point(0, 0));
        if (source is not null)
        {
            var logical = source.TransformFromDevice.Transform(topLeft);
            Left = logical.X;
            Top = logical.Y - Height - 8;
        }
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 4, Math.Min(Left, workArea.Right - Width - 4));
        Top = Math.Max(workArea.Top + 4, Math.Min(Top, workArea.Bottom - Height - 4));
        Show();
        Activate();
        ToastService.MarkAllRead();
    }

    private void History_Changed(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void UpdateEmptyState() =>
        EmptyText.Visibility = ToastService.History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Clear_Click(object sender, RoutedEventArgs e) => ToastService.ClearHistory();

    private void PrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ToastMessage item } && item.PrimaryAction is not null) item.PrimaryAction();
    }

    private void SecondaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ToastMessage item } && item.SecondaryAction is not null) item.SecondaryAction();
    }
}

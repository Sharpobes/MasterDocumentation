using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MasterDocumentation.UI;
using MasterDocumentation.Utilities;

namespace MasterDocumentation.Views;

public enum ToastKind
{
    Information,
    Success,
    Warning,
    Error
}

public sealed record ToastMessage(
    Guid Id,
    string Title,
    string Message,
    AppIconKind Icon,
    Brush Accent,
    string? PrimaryActionText,
    Action? PrimaryAction,
    string? SecondaryActionText,
    Action? SecondaryAction,
    DateTime CreatedAt)
{
    public bool HasPrimaryAction => !string.IsNullOrWhiteSpace(PrimaryActionText) && PrimaryAction is not null;
    public bool HasSecondaryAction => !string.IsNullOrWhiteSpace(SecondaryActionText) && SecondaryAction is not null;
    public string TimeText => CreatedAt.ToString("HH:mm");
}

public partial class ToastHost : UserControl
{
    private readonly ObservableCollection<ToastMessage> _items = [];
    private readonly Dictionary<Guid, DispatcherTimer> _timers = [];

    /// <summary>Количество видимых уведомлений изменилось — окно уведомлений подстраивает размер и видимость.</summary>
    public event EventHandler? ItemsChanged;

    public int Count => _items.Count;

    public ToastHost()
    {
        InitializeComponent();
        ToastItems.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Show(
        string title,
        string message,
        ToastKind kind = ToastKind.Information,
        TimeSpan? duration = null,
        string? primaryActionText = null,
        Action? primaryAction = null,
        string? secondaryActionText = null,
        Action? secondaryAction = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Show(title, message, kind, duration, primaryActionText, primaryAction, secondaryActionText, secondaryAction));
            return;
        }

        var (icon, brushKey) = kind switch
        {
            ToastKind.Success => (AppIconKind.Success, "State/Success"),
            ToastKind.Warning => (AppIconKind.Warning, "State/Warning"),
            ToastKind.Error => (AppIconKind.Error, "State/Error"),
            _ => (AppIconKind.Info, "State/Info")
        };
        var brush = TryFindResource(brushKey) as Brush ?? Brushes.CornflowerBlue;
        var item = new ToastMessage(Guid.NewGuid(), title, message, icon, brush, primaryActionText, primaryAction, secondaryActionText, secondaryAction, DateTime.Now);
        ToastService.Remember(item);
        _items.Add(item);
        while (_items.Count > 4)
            Remove(_items[0]);

        // Карточка исчезает сама: всё, что было показано, остаётся в центре уведомлений.
        var timer = new DispatcherTimer { Interval = duration ?? (kind == ToastKind.Error ? TimeSpan.FromSeconds(9) : TimeSpan.FromSeconds(3)) };
        timer.Tick += (_, _) =>
        {
            Remove(item);
        };
        _timers[item.Id] = timer;
        timer.Start();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ToastMessage item })
            Remove(item);
    }

    private void PrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ToastMessage item } || item.PrimaryAction is null)
            return;
        item.PrimaryAction();
        Remove(item);
    }

    private void SecondaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ToastMessage item } || item.SecondaryAction is null)
            return;
        item.SecondaryAction();
        Remove(item);
    }

    private void Toast_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ToastMessage item && _timers.TryGetValue(item.Id, out var timer))
            timer.Stop();
    }

    private void Toast_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ToastMessage item && _timers.TryGetValue(item.Id, out var timer))
            timer.Start();
    }

    private void Remove(ToastMessage item)
    {
        if (_timers.Remove(item.Id, out var timer))
            timer.Stop();
        _items.Remove(item);
    }
}

public static class ToastService
{
    private const int HistoryLimit = 100;
    private static Window? _owner;
    private static ToastWindow? _window;
    private static int _unread;

    /// <summary>Все показанные уведомления, новые сверху: карточка исчезает, запись остаётся.</summary>
    public static ObservableCollection<ToastMessage> History { get; } = [];

    /// <summary>Количество уведомлений, показанных после последнего открытия центра уведомлений.</summary>
    public static int Unread => _unread;

    public static event EventHandler? UnreadChanged;

    /// <summary>Привязывает уведомления к главному окну приложения.</summary>
    public static void Initialize(Window owner)
    {
        _owner = owner;
        _window = null;
        owner.Closed += (_, _) => { _window = null; _owner = null; };
    }

    internal static void Remember(ToastMessage item)
    {
        History.Insert(0, item);
        while (History.Count > HistoryLimit) History.RemoveAt(History.Count - 1);
        _unread++;
        UnreadChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void MarkAllRead()
    {
        if (_unread == 0) return;
        _unread = 0;
        UnreadChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void ClearHistory()
    {
        History.Clear();
        MarkAllRead();
    }

    public static void Show(
        string title,
        string message,
        ToastKind kind = ToastKind.Information,
        TimeSpan? duration = null,
        string? primaryActionText = null,
        Action? primaryAction = null,
        string? secondaryActionText = null,
        Action? secondaryAction = null)
    {
        var owner = _owner;
        if (owner is null) return;
        if (!owner.Dispatcher.CheckAccess())
        {
            owner.Dispatcher.Invoke(() => Show(title, message, kind, duration, primaryActionText, primaryAction, secondaryActionText, secondaryAction));
            return;
        }
        _window ??= new ToastWindow(owner);
        _window.Host.Show(LocalizationService.T(title),LocalizationService.T(message),kind,duration,
            primaryActionText is null?null:LocalizationService.T(primaryActionText),primaryAction,
            secondaryActionText is null?null:LocalizationService.T(secondaryActionText),secondaryAction);
    }
}

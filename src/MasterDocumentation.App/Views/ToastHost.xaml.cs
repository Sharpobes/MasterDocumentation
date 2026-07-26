using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MasterDocumentation.UI;

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
    Action? SecondaryAction)
{
    public bool HasPrimaryAction => !string.IsNullOrWhiteSpace(PrimaryActionText) && PrimaryAction is not null;
    public bool HasSecondaryAction => !string.IsNullOrWhiteSpace(SecondaryActionText) && SecondaryAction is not null;
}

public partial class ToastHost : UserControl
{
    private readonly ObservableCollection<ToastMessage> _items = [];
    private readonly Dictionary<Guid, DispatcherTimer> _timers = [];

    public ToastHost()
    {
        InitializeComponent();
        ToastItems.ItemsSource = _items;
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
        var item = new ToastMessage(Guid.NewGuid(), title, message, icon, brush, primaryActionText, primaryAction, secondaryActionText, secondaryAction);
        _items.Add(item);
        while (_items.Count > 4)
            Remove(_items[0]);

        var timer = new DispatcherTimer { Interval = duration ?? (kind == ToastKind.Error ? TimeSpan.FromSeconds(9) : TimeSpan.FromSeconds(5)) };
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
    private static WeakReference<ToastHost>? _host;

    public static void Initialize(ToastHost host) => _host = new WeakReference<ToastHost>(host);

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
        if (_host?.TryGetTarget(out var host) == true)
            host.Show(title, message, kind, duration, primaryActionText, primaryAction, secondaryActionText, secondaryAction);
    }
}

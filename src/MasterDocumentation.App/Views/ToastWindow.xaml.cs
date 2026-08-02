using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MasterDocumentation.Views;

/// <summary>
/// Отдельное окно уведомлений. Уведомления вынесены из главного окна по трём причинам:
/// они должны быть видны поверх всех окон (включая встроенный редактор на WebView2, который
/// рисуется собственным HWND поверх любых элементов WPF, и поверх модальных диалогов),
/// не должны перехватывать фокус и мешать работе (окно создаётся с WS_EX_NOACTIVATE и
/// занимает ровно размер карточек), и должны показывать видимую кнопку закрытия.
/// </summary>
public partial class ToastWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const double ScreenMargin = 8;

    private readonly Window _owner;
    private bool _closing;

    public ToastHost Host => Notifications;

    public ToastWindow(Window owner)
    {
        InitializeComponent();
        _owner = owner;
        Notifications.ItemsChanged += (_, _) => Sync();
        SizeChanged += (_, _) => UpdatePosition();
        _owner.LocationChanged += OwnerChanged;
        _owner.SizeChanged += OwnerChanged;
        _owner.StateChanged += OwnerChanged;
        _owner.IsVisibleChanged += OwnerVisibilityChanged;
        _owner.Closed += (_, _) => CloseHost();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
    }

    /// <summary>Уведомления не закрывают приложение и не остаются после закрытия главного окна.</summary>
    public void CloseHost()
    {
        if (_closing) return;
        _closing = true;
        _owner.LocationChanged -= OwnerChanged;
        _owner.SizeChanged -= OwnerChanged;
        _owner.StateChanged -= OwnerChanged;
        _owner.IsVisibleChanged -= OwnerVisibilityChanged;
        Close();
    }

    private void OwnerChanged(object? sender, EventArgs e) => Sync();
    private void OwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Sync();

    private void Sync()
    {
        if (_closing) return;
        if (Notifications.Count == 0 || !_owner.IsVisible || _owner.WindowState == WindowState.Minimized)
        {
            if (IsVisible) Hide();
            return;
        }
        if (!IsVisible) Show();
        UpdatePosition();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdatePosition));
    }

    /// <summary>
    /// Позиционирование выполняется в физических пикселях: главное окно и окно уведомлений
    /// могут находиться на мониторах с разным масштабированием, а Left/Top в WPF задаются
    /// в независимых единицах основного монитора.
    /// </summary>
    private void UpdatePosition()
    {
        if (_closing || !IsVisible) return;
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            var self = PresentationSource.FromVisual(this)?.CompositionTarget;
            if (self is null) return;
            var size = self.TransformToDevice.Transform(new Point(ActualWidth, ActualHeight));
            var margin = self.TransformToDevice.Transform(new Point(ScreenMargin, ScreenMargin));

            Point anchor;
            if (PresentationSource.FromVisual(_owner) is not null && _owner.ActualWidth > 0)
                anchor = _owner.PointToScreen(new Point(_owner.ActualWidth, _owner.ActualHeight));
            else
            {
                var workArea = SystemParameters.WorkArea;
                anchor = self.TransformToDevice.Transform(new Point(workArea.Right, workArea.Bottom));
            }

            SetWindowPos(handle, HwndTopmost, (int)(anchor.X - size.X - margin.X), (int)(anchor.Y - size.Y - margin.Y), 0, 0, SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
        }
        catch (InvalidOperationException)
        {
            // Главное окно ещё не отображено — позиция будет уточнена при следующем уведомлении.
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}

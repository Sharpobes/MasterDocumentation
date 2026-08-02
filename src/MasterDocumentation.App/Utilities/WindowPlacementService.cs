using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MasterDocumentation.Utilities;

public static class WindowPlacementService
{
    private const int MinimumVisibleWidth = 120;
    private const int MinimumVisibleHeight = 80;
    private const int GwlExStyle = -20;
    private const int WsExAppWindow = 0x00040000;

    /// <summary>
    /// Подготавливает окно, которое открывается до главного окна (первый запуск, ошибка хранилища).
    /// Модальное окно без владельца WPF делает дочерним для скрытого служебного окна: кнопка на
    /// панели задач пропадает, а CenterScreen отсчитывается не от основного монитора — окно уходит
    /// на второй экран. Со стороны это выглядит как «приложение не запускается»: процесс есть,
    /// а на экране и на панели задач ничего нет. Поэтому окно центрируется на основном мониторе
    /// и принудительно получает собственную кнопку на панели задач.
    /// </summary>
    public static void PrepareOwnerlessWindow(Window window)
    {
        window.ShowInTaskbar = true;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        CenterOnPrimaryWorkArea(window);
        // Размер окна по содержимому (SizeToContent) известен только после вёрстки, которая
        // выполняется до вывода окна на экран: пересчёт здесь не приводит к рывку.
        window.SizeChanged += (_, _) => { if (!window.IsVisible) CenterOnPrimaryWorkArea(window); };
        window.SourceInitialized += (_, _) => ForceTaskbarButton(window);
    }

    private static void CenterOnPrimaryWorkArea(Window window)
    {
        var area = SystemParameters.WorkArea;
        var width = Resolve(window.Width, window.ActualWidth, window.MinWidth);
        var height = Resolve(window.Height, window.ActualHeight, window.MinHeight);
        window.Left = area.Left + Math.Max(0, (area.Width - width) / 2);
        window.Top = area.Top + Math.Max(0, (area.Height - height) / 2);
    }

    private static double Resolve(double requested, double actual, double minimum) =>
        !double.IsNaN(requested) ? requested : actual > 0 ? actual : minimum;

    /// <summary>Кнопка на панели задач: у окна с владельцем её создаёт только WS_EX_APPWINDOW.</summary>
    private static void ForceTaskbarButton(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLong(handle, GwlExStyle);
        if ((style & WsExAppWindow) == 0)
            SetWindowLong(handle, GwlExStyle, style | WsExAppWindow);
    }

    public static void RestoreVisibleBounds(Window window, double left, double top, double width, double height)
    {
        var requested = new Rect(left, top, Math.Max(window.MinWidth, width), Math.Max(window.MinHeight, height));
        var workAreas = GetWorkAreas();
        if (workAreas.Any(area =>
        {
            var intersection = Rect.Intersect(requested, area);
            return intersection.Width >= MinimumVisibleWidth && intersection.Height >= MinimumVisibleHeight;
        }))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = requested.Left;
            window.Top = requested.Top;
            window.Width = requested.Width;
            window.Height = requested.Height;
            return;
        }

        var primary = SystemParameters.WorkArea;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Width = Math.Min(requested.Width, primary.Width);
        window.Height = Math.Min(requested.Height, primary.Height);
        window.Left = primary.Left + (primary.Width - window.Width) / 2;
        window.Top = primary.Top + (primary.Height - window.Height) / 2;
    }

    private static IReadOnlyList<Rect> GetWorkAreas()
    {
        var areas = new List<Rect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
                areas.Add(new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top));
            return true;
        }, IntPtr.Zero);
        if (areas.Count == 0)
            areas.Add(SystemParameters.WorkArea);
        return areas;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
    }
}

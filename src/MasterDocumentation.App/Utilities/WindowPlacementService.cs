using System.Runtime.InteropServices;
using System.Windows;

namespace MasterDocumentation.Utilities;

public static class WindowPlacementService
{
    private const int MinimumVisibleWidth = 120;
    private const int MinimumVisibleHeight = 80;

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

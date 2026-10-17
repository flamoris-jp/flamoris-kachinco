using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Kachinco.Infrastructure;

namespace Kachinco.App;

public partial class MainWindow
{
    private readonly WindowPlacementPreferences windowPlacement = new();
    private void InitializeWindowPlacement()
    {
        var saved = windowPlacement.Load();
        if (saved is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = saved.Left; Top = saved.Top; Width = saved.Width; Height = saved.Height;
        }
        // Clamp physical coordinates after Windows chooses per-monitor DPI. Work
        // areas may have negative origins and may be smaller than WPF minimum size.
        SourceInitialized += (_, _) =>
        {
            ClampToMonitor();
            if (saved?.Maximized == true) WindowState = WindowState.Maximized;
        };
    }
    private void SaveWindowPlacement()
    {
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        windowPlacement.Save(new(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized));
    }
    private void ClampToMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(hwnd, out var rect)) return;
        var monitor = MonitorFromWindow(hwnd, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
        var area = info.Work;
        int width = Math.Min(rect.Right - rect.Left, area.Right - area.Left);
        int height = Math.Min(rect.Bottom - rect.Top, area.Bottom - area.Top);
        int left = Math.Clamp(rect.Left, area.Left, area.Right - width);
        int top = Math.Clamp(rect.Top, area.Top, area.Bottom - height);
        SetWindowPos(hwnd, IntPtr.Zero, left, top, width, height, 0x14);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}

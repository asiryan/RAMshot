using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using RAMshot.ViewModels;

namespace RAMshot;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private bool shutdown;
    internal MainViewModel ViewModel => viewModel;
    public MainWindow(IntPtr initialWindow)
    {
        InitializeComponent();
        viewModel = new MainViewModel(initialWindow);
        DataContext = viewModel;
    }
    public void ShowAndActivate(IntPtr target)
    {
        if (target != IntPtr.Zero) viewModel.SelectWindow(target);
        Show(); WindowState = WindowState.Normal; Activate();
    }
    public void Shutdown() { shutdown = true; viewModel.Dispose(); Close(); }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr handle = new WindowInteropHelper(this).Handle;
        int enabled = 1;
        // Windows 10 2004+ and Windows 11; earlier versions keep the system title bar.
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
    }
    // WindowChrome maximizes a borderless window to the whole monitor; keep it inside the work area.
    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0024 && lParam != IntPtr.Zero)
        {
            MinMaxInfo info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            IntPtr monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (monitor != IntPtr.Zero)
            {
                var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref monitorInfo))
                {
                    info.MaxPosition.X = monitorInfo.Work.Left - monitorInfo.Monitor.Left;
                    info.MaxPosition.Y = monitorInfo.Work.Top - monitorInfo.Monitor.Top;
                    info.MaxSize.X = monitorInfo.Work.Right - monitorInfo.Work.Left;
                    info.MaxSize.Y = monitorInfo.Work.Bottom - monitorInfo.Work.Top;
                    Marshal.StructureToPtr(info, lParam, true);
                }
            }
            handled = true;
        }
        return IntPtr.Zero;
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!shutdown) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (MaximizeGlyph is not null)
            MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}

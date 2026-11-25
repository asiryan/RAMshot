using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace RAMshot;

public partial class App : Application
{
    private Mutex? instance;
    private Forms.NotifyIcon? tray;
    private GlobalHotkey? hotkey;
    private DispatcherTimer? foregroundTimer;
    private MainWindow? window;
    private IntPtr lastForeground;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            instance = new Mutex(true, @"Local\RAMshot.SingleInstance", out bool created);
            if (!created)
            {
                MessageBox.Show("RAMshot is already running. Open it from the system tray.", "RAMshot", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open RAMshot", null, (_, _) => ShowWindow(lastForeground));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => ExitApplication());
            var iconStream = GetResourceStream(new Uri("pack://application:,,,/RAMshot;component/Assets/RAMshot.ico"))?.Stream;
            using (iconStream)
            {
                using var icon = iconStream is null ? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone() : new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize);
                tray = new Forms.NotifyIcon { Icon = (System.Drawing.Icon)icon.Clone(), Text = "RAMshot", ContextMenuStrip = menu, Visible = true };
            }
            tray.DoubleClick += (_, _) => ShowWindow(lastForeground);
            foregroundTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            foregroundTimer.Tick += (_, _) => RememberForeground();
            RememberForeground();
            foregroundTimer.Start();
            window = new MainWindow(NativeWindows.IsWindow(lastForeground) ? lastForeground : IntPtr.Zero);
            window.Show();
            window.ViewModel.SettingsChanged += OnSettingsChanged;
            ConfigureHotkey();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"RAMshot could not start (error 0x{ex.HResult:X8}).", "RAMshot", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => ConfigureHotkey();

    private void ConfigureHotkey()
    {
        if (window is null || !window.ViewModel.IsHotkeyEnabled)
        {
            hotkey?.Dispose();
            hotkey = null;
            return;
        }
        if (hotkey is not null) return;
        try
        {
            hotkey = new GlobalHotkey(() =>
            {
                IntPtr target = NativeWindows.GetForegroundWindow();
                Dispatcher.BeginInvoke(() =>
                {
                    RememberForeground(target);
                    ShowWindow(lastForeground);
                });
            });
        }
        catch (Win32Exception)
        {
            tray?.ShowBalloonTip(5000, "RAMshot", "The global hotkey is unavailable. Open RAMshot from the tray.", Forms.ToolTipIcon.Warning);
        }
    }

    private void RememberForeground() => RememberForeground(NativeWindows.GetForegroundWindow());

    private void RememberForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeWindows.IsWindowVisible(hwnd)) return;
        NativeWindows.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return;
        var name = new StringBuilder(128);
        NativeWindows.GetClassName(hwnd, name, name.Capacity);
        if (name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "#32768") return;
        lastForeground = hwnd;
    }

    private void ShowWindow(IntPtr target)
    {
        if (window is null) return;
        window.ShowAndActivate(NativeWindows.IsWindow(target) ? target : IntPtr.Zero);
    }

    private void ExitApplication()
    {
        window?.Shutdown();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (window is not null) window.ViewModel.SettingsChanged -= OnSettingsChanged;
        hotkey?.Dispose();
        foregroundTimer?.Stop();
        if (tray is not null)
        {
            tray.Visible = false;
            tray.ContextMenuStrip?.Dispose();
            tray.Icon?.Dispose();
            tray.Dispose();
        }
        instance?.Dispose();
        base.OnExit(e);
    }
}

internal static class NativeWindows
{
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);
}

internal sealed class GlobalHotkey : IDisposable
{
    private delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
    private readonly HookCallback callback;
    private readonly Action action;
    private IntPtr hook;
    private bool down;

    internal GlobalHotkey(Action action)
    {
        this.action = action;
        callback = OnKeyboard;
        hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && Marshal.ReadInt32(data) == 0x7B)
        {
            long kind = message.ToInt64();
            if (kind is 0x101 or 0x105) down = false;
            else if (kind is 0x100 or 0x104)
            {
                bool first = !down;
                down = true;
                if (first && (GetAsyncKeyState(0x11) & 0x8000) != 0 && (GetAsyncKeyState(0x10) & 0x8000) != 0) action();
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }

    public void Dispose() { if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; } }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookCallback proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
}

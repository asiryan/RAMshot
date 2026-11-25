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
        viewModel.HideRequested += (_, _) => Hide();
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
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!shutdown) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

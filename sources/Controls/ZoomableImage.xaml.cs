using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RAMshot.Controls;

// Shows the selected image on a checkerboard and supports wheel zoom, drag panning and double-click reset.
public partial class ZoomableImage : UserControl
{
    private const double MinScale = 1.0;
    private const double MaxScale = 8.0;
    private double scale = MinScale;
    private Vector pan;
    private bool dragging;
    private Point dragStart;
    private Vector panStart;

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(ZoomableImage), new PropertyMetadata(null, OnSourceChanged));
    public static readonly DependencyProperty SelectionKeyProperty = DependencyProperty.Register(
        nameof(SelectionKey), typeof(object), typeof(ZoomableImage), new PropertyMetadata(null, OnSelectionKeyChanged));

    public ZoomableImage() => InitializeComponent();

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    // Changing the selected image resets zoom and pan; animation frames keep the current view.
    public object? SelectionKey
    {
        get => GetValue(SelectionKeyProperty);
        set => SetValue(SelectionKeyProperty, value);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (ZoomableImage)d;
        view.PreviewImage.Source = (ImageSource?)e.NewValue;
        view.UpdateFitSize();
    }

    private static void OnSelectionKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ZoomableImage)d).ResetView();

    private void ResetView()
    {
        scale = MinScale;
        pan = default;
        UpdateFitSize();
        ApplyTransform();
    }

    private void UpdateFitSize()
    {
        ImageSource? source = PreviewImage.Source;
        double viewerWidth = Viewport.ActualWidth, viewerHeight = Viewport.ActualHeight;
        if (source is null || viewerWidth <= 0 || viewerHeight <= 0 ||
            double.IsNaN(source.Width) || double.IsNaN(source.Height) ||
            double.IsInfinity(source.Width) || double.IsInfinity(source.Height) ||
            source.Width <= 0 || source.Height <= 0)
        {
            ImageHost.Width = 0;
            ImageHost.Height = 0;
            return;
        }
        double fit = Math.Min(viewerWidth / source.Width, viewerHeight / source.Height);
        ImageHost.Width = source.Width * fit;
        ImageHost.Height = source.Height * fit;
        ApplyTransform();
    }

    private void ApplyTransform()
    {
        ContentScale.ScaleX = scale;
        ContentScale.ScaleY = scale;
        double maxX = Math.Max(0, (ImageHost.Width * scale - Viewport.ActualWidth) / 2);
        double maxY = Math.Max(0, (ImageHost.Height * scale - Viewport.ActualHeight) / 2);
        pan.X = Math.Clamp(pan.X, -maxX, maxX);
        pan.Y = Math.Clamp(pan.Y, -maxY, maxY);
        ContentTranslate.X = pan.X;
        ContentTranslate.Y = pan.Y;
    }

    private void OnViewerSizeChanged(object sender, SizeChangedEventArgs e) => UpdateFitSize();

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (PreviewImage.Source is null || Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return;
        double factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        double next = Math.Clamp(scale * factor, MinScale, MaxScale);
        if (Math.Abs(next - scale) < 0.0001) return;
        Point position = e.GetPosition(Viewport);
        var center = new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2);
        Vector cursor = position - center;
        Vector offset = cursor - pan;
        pan = cursor - offset * (next / scale);
        scale = next;
        if (scale <= MinScale + 0.0001) pan = default;
        ApplyTransform();
        e.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // Handle the double click here: marking the button-down event handled can suppress the MouseDoubleClick event.
        if (e.ClickCount == 2)
        {
            ResetView();
            e.Handled = true;
            return;
        }
        if (PreviewImage.Source is null || scale <= MinScale + 0.0001) return;
        Focus();
        dragging = true;
        dragStart = e.GetPosition(Viewport);
        panStart = pan;
        CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!dragging) return;
        pan = panStart + (e.GetPosition(Viewport) - dragStart);
        ApplyTransform();
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Hand;
        e.Handled = true;
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        dragging = false;
        Cursor = Cursors.Hand;
    }
}

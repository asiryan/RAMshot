using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace RAMshot.Controls;

// One hover hint implementation for the whole window; only the text differs per element.
public static class Hint
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Hint), new PropertyMetadata(null, OnTextChanged));
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(Hint),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits, OnIsEnabledChanged));

    private static readonly ConditionalWeakTable<FrameworkElement, HintState> States = new();
    private static Popup? hintPopup;
    private static HintControl? hintControl;
    private static HintState? currentState;

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);
    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if (e.NewValue is string text && !string.IsNullOrWhiteSpace(text))
        {
            var state = States.GetValue(element, key => new HintState(key));
            state.Text = text;
        }
        else if (States.TryGetValue(element, out var state))
        {
            state.Text = null;
            state.Hide();
        }
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue) HideAll();
    }

    private static void Show(HintState state, string text)
    {
        hintControl ??= new HintControl();
        hintPopup ??= new Popup
        {
            AllowsTransparency = true,
            StaysOpen = true,
            IsHitTestVisible = false,
            Placement = PlacementMode.MousePoint,
            HorizontalOffset = 8,
            VerticalOffset = 10,
            PopupAnimation = PopupAnimation.Fade,
            Child = hintControl
        };
        hintControl.Text = text;
        currentState = state;
        // Reopen so the popup is placed at the current pointer position.
        hintPopup.IsOpen = false;
        hintPopup.IsOpen = true;
    }

    private static void Hide(HintState state)
    {
        if (!ReferenceEquals(currentState, state)) return;
        HideAll();
    }

    public static void HideAll()
    {
        if (hintPopup is not null) hintPopup.IsOpen = false;
        currentState = null;
    }

    private sealed class HintState
    {
        private readonly FrameworkElement element;
        private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };

        public HintState(FrameworkElement element)
        {
            this.element = element;
            timer.Tick += OnTimerTick;
            // Also observe events that the target control marks as handled (for example wheel zoom).
            element.AddHandler(Mouse.MouseEnterEvent, new MouseEventHandler(OnMouseEnter), true);
            element.AddHandler(Mouse.MouseLeaveEvent, new MouseEventHandler(OnMouseLeave), true);
            element.AddHandler(UIElement.MouseWheelEvent, new MouseWheelEventHandler(OnMouseWheel), true);
            element.AddHandler(UIElement.MouseDownEvent, new MouseButtonEventHandler(OnMouseDown), true);
            element.Unloaded += OnUnloaded;
        }

        public string? Text { get; set; }

        private void OnMouseEnter(object sender, MouseEventArgs e) => Start();
        private void OnMouseLeave(object sender, MouseEventArgs e) => Hide();
        private void OnMouseWheel(object sender, MouseWheelEventArgs e) => Hide();
        private void OnMouseDown(object sender, MouseButtonEventArgs e) => Hide();
        private void OnUnloaded(object sender, RoutedEventArgs e) => Hide();

        private void Start()
        {
            if (string.IsNullOrWhiteSpace(Text) || !GetIsEnabled(element) || !element.IsVisible) return;
            timer.Stop();
            timer.Start();
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            timer.Stop();
            if (!string.IsNullOrWhiteSpace(Text) && GetIsEnabled(element) && element.IsMouseOver && element.IsVisible)
                Show(this, Text);
        }

        public void Hide()
        {
            timer.Stop();
            Hint.Hide(this);
        }
    }
}

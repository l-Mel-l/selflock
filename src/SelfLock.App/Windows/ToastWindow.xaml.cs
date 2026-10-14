using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SelfLock.Native;

namespace SelfLock.Windows;

/// <summary>Короткое уведомление в правом нижнем углу. Не забирает фокус и само исчезает.</summary>
public partial class ToastWindow
{
    private static ToastWindow? _current;
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(6) };

    private ToastWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NoActivate.Apply(this);
        _hide.Tick += (_, _) => Close();
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = Services.TestMode.Offscreen ? Services.TestMode.OffscreenLeft : wa.Right - ActualWidth - 8;
            Top = wa.Bottom - ActualHeight - 8;
            Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
                new DoubleAnimation(60, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase() });
        };
        Closed += (_, _) =>
        {
            _hide.Stop();
            if (_current == this) _current = null;
        };
    }

    public static void Show(string title, string text, string glyph = "")
    {
        _current?.Close();
        var w = new ToastWindow();
        w.TitleText.Text = title;
        w.BodyText.Text = text;
        w.Glyph.Text = glyph;
        _current = w;
        w.Show();
        w._hide.Start();
    }

    private void Dismiss(object sender, MouseButtonEventArgs e) => Close();
}

internal static class NoActivate
{
    /// <summary>Окно поверх всех, но клик по нему не уводит фокус из браузера/приложения.</summary>
    public static void Apply(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        var ex = Win32.GetWindowLong(h, Win32.GWL_EXSTYLE);
        Win32.SetWindowLong(h, Win32.GWL_EXSTYLE, ex | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
    }
}

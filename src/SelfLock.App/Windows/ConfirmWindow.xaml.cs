using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SelfLock.UI;

namespace SelfLock.Windows;

/// <summary>Подтверждение ослабления защиты с обязательной паузой.</summary>
public partial class ConfirmWindow
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DateTime _readyAt;
    private readonly int _delay;
    private readonly string _action;

    private ConfirmWindow(string title, string text, int delaySeconds, string action)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = text;
        _delay = delaySeconds;
        _action = action;
        _readyAt = DateTime.Now.AddSeconds(delaySeconds);
        if (delaySeconds <= 0) DelayBox.Visibility = Visibility.Collapsed;
        _timer.Tick += (_, _) => Update();
        Update();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    public static bool Ask(Window? owner, string title, string text, int delaySeconds, string action = "Подтвердить")
    {
        var w = new ConfirmWindow(title, text, delaySeconds, action);
        Dialogs.Place(w, owner);
        return w.ShowDialog() == true;
    }

    private void Update()
    {
        var left = _readyAt - DateTime.Now;
        if (left <= TimeSpan.Zero)
        {
            OkButton.IsEnabled = true;
            OkButton.Content = _action;
            DelayBar.Value = 1;
            SecondsRun.Text = "0 сек";
            _timer.Stop();
            return;
        }
        var s = (int)Math.Ceiling(left.TotalSeconds);
        OkButton.IsEnabled = false;
        OkButton.Content = $"{_action} ({s})";
        SecondsRun.Text = $"{s} сек";
        DelayBar.Value = 1 - left.TotalSeconds / _delay;
    }

    private void Ok(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Drag(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}

public static class Dialogs
{
    public static void Place(Window w, Window? owner)
    {
        if (owner is { IsVisible: true })
        {
            w.Owner = owner;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.Icon = AppIcon;
        if (Services.TestMode.Offscreen) w.ShowActivated = false;
    }

    public static readonly System.Windows.Media.ImageSource AppIcon =
        System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/selflock.ico"));
}

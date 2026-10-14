using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SelfLock.Core;
using SelfLock.UI;

namespace SelfLock.Windows;

/// <summary>
/// Предупреждение мягкого режима (поверх всех окон, без перехвата фокуса):
/// однократная отсрочка на заданное в правиле время или закрытие сразу.
/// </summary>
public partial class WarningWindow
{
    private static int _open;
    private readonly AppHost _host;
    private readonly Rule _rule;
    private readonly DateTime _deadline;
    private readonly double _total;

    public WarningWindow(AppHost host, Rule rule, DateTime deadline, bool canSnooze)
    {
        InitializeComponent();
        _host = host;
        _rule = rule;
        _deadline = deadline;
        _total = Math.Max(1, (deadline - DateTime.Now).TotalSeconds);
        Icon = Dialogs.AppIcon;

        var now = DateTime.Now;
        BodyText.Text = rule.Condition switch
        {
            ConditionKind.TimeWindow => $"Сейчас запрещённое время для «{rule.Name}» ({Format.Hm(rule.WindowStartMinutes)}–{Format.Hm(rule.WindowEndMinutes)}).",
            ConditionKind.DailyTotal => $"Сегодня вы провели в «{rule.Name}» {Format.Duration(host.Engine.UsedSeconds(rule, now))} — это ваш дневной лимит.",
            _ => $"Вы провели в «{rule.Name}» {Format.Duration(host.Engine.UsedSeconds(rule, now))} без перерыва.",
        };
        CountdownLabel.Text = rule.Kind == TargetKind.App ? "Приложение закроется через" : "Вкладка закроется через";

        if (canSnooze)
        {
            if (rule.SnoozeChoosable && rule.SnoozeMinutes > 1)
            {
                ChoosePanel.Visibility = Visibility.Visible;
                SnoozeStepper.Minimum = 1;
                SnoozeStepper.Maximum = rule.SnoozeMinutes;
                SnoozeStepper.ValueChanged += v => SnoozeButton.Content = $"Отложить ({v} мин)";
                SnoozeStepper.Value = rule.SnoozeMinutes;
            }
            SnoozeButton.Content = $"Отложить ({rule.SnoozeMinutes} мин)";
        }
        else
        {
            SnoozeButton.Visibility = Visibility.Collapsed;
            SnoozeCol.Width = new GridLength(0);
            UsedText.Visibility = Visibility.Visible;
            CloseButton.Style = (Style)FindResource("BtnPrimary");
            CloseButton.MinWidth = 320;
        }

        SourceInitialized += (_, _) => NoActivate.Apply(this);
        var slot = _open++;
        Closed += (_, _) => _open--;
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = Services.TestMode.Offscreen ? Services.TestMode.OffscreenLeft : wa.Right - ActualWidth - 8;
            Top = wa.Bottom - ActualHeight - 8 - slot * 40;
            Slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = new BackEase { Amplitude = 0.4 } });
            if (!Services.TestMode.Offscreen) System.Media.SystemSounds.Exclamation.Play();
        };
        Refresh(now);
    }

    public void Refresh(DateTime now)
    {
        var left = _deadline - now;
        CountdownText.Text = Format.Countdown(left);
        CountdownBar.Value = Math.Clamp(left.TotalSeconds / _total, 0, 1);
    }

    private void SnoozeClick(object sender, RoutedEventArgs e) =>
        _host.Snooze(_rule, ChoosePanel.Visibility == Visibility.Visible ? SnoozeStepper.Value : null);

    private void CloseClick(object sender, RoutedEventArgs e) => _host.CloseNow(_rule);
}

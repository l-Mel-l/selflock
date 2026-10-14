using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SelfLock.Core;
using SelfLock.UI;

namespace SelfLock.Windows;

/// <summary>Экран блокировки: показывается после закрытия ресурса, перекрывает основной монитор.</summary>
public partial class BlockWindow
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime? _until;

    public BlockWindow()
    {
        InitializeComponent();
        Icon = Dialogs.AppIcon;
        Left = Services.TestMode.Offscreen ? Services.TestMode.OffscreenLeft : 0;
        Top = 0;
        ShowActivated = !Services.TestMode.Offscreen;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        _timer.Tick += (_, _) => UpdateUntil();
        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
            Rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase() });
        };
        Closed += (_, _) => _timer.Stop();
        _timer.Start();
    }

    public void Set(Rule rule, RuleState state, double usedSeconds)
    {
        TitleText.Text = rule.Kind == TargetKind.App ? "Приложение заблокировано" : "Сайт заблокирован";
        BodyText.Text = rule.Condition == ConditionKind.TimeWindow
            ? $"Сейчас запрещённое время для «{rule.Name}». Вы сами так решили — и это хорошее решение."
            : $"Вы превысили установленный лимит времени для «{rule.Name}».";
        LimitText.Text = rule.Condition switch
        {
            ConditionKind.Continuous => $"Лимит: {Format.Duration(rule.LimitMinutes * 60)} подряд",
            ConditionKind.DailyTotal => $"Лимит: {Format.Duration(rule.LimitMinutes * 60)} в день",
            _ => $"Запрещено: {Format.Hm(rule.WindowStartMinutes)}–{Format.Hm(rule.WindowEndMinutes)}",
        };
        SpentText.Text = $"Потрачено сегодня: {Format.Duration(AppHost.Current?.Store.Usage.RuleSeconds(DateTime.Now, rule.Id) ?? usedSeconds)}";
        _until = state.Until;
        UpdateUntil();
    }

    private void UpdateUntil()
    {
        if (_until is not { } u) { UntilText.Text = ""; return; }
        var now = DateTime.Now;
        UntilText.Text = u <= now ? "Блокировка закончилась" : $"Доступ откроется {Format.At(u, now)} (через {Format.Duration((u - now).TotalSeconds)})";
    }

    private void Back(object sender, RoutedEventArgs e) => Close();

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Enter) Close();
    }
}

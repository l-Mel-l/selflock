using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using SelfLock.Core;
using SelfLock.UI;

namespace SelfLock.Windows;

/// <summary>Строка правила для списков; обновляется каждую секунду.</summary>
public sealed class RuleRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public Rule Rule { get; }
    public Look Look { get; }
    public string Name => Rule.Name;
    public string Target => Format.Target(Rule);
    public string Condition => Format.Condition(Rule);
    public string Mode => Format.Mode(Rule);
    public Brush ModeBg => Visuals.Hex(Rule.Mode == ReactionMode.Hard ? "#3D1830" : "#2A2466");
    public Brush ModeFg => Visuals.Hex(Rule.Mode == ReactionMode.Hard ? "#FF6F89" : "#B9A8FF");
    public bool Enabled => Rule.Enabled;

    public double Progress { get; private set; }
    public Brush ProgressBrush { get; private set; } = Brushes.Transparent;
    public string Right { get; private set; } = "";
    public string State { get; private set; } = "";
    public Brush StateBg { get; private set; } = Brushes.Transparent;
    public Brush StateFg { get; private set; } = Brushes.White;
    /// <summary>Для сортировки: чем важнее, тем меньше.</summary>
    public int Order { get; private set; }

    private static readonly Brush Accent = (Brush)Application.Current.FindResource("AccentGradH");
    private static readonly Brush Hot = new LinearGradientBrush(
        (Color)ColorConverter.ConvertFromString("#FFB648"), (Color)ColorConverter.ConvertFromString("#FF4D6D"), 0);
    private static readonly Brush RedB = Visuals.Hex("#FF4D6D");

    public RuleRow(Rule rule)
    {
        Rule = rule;
        Look = Visuals.ForRule(rule);
    }

    public void Update(AppHost host, DateTime now)
    {
        var st = host.Engine.StateOf(Rule);
        var running = host.Engine.IsRunning(now);
        var active = Matcher.Matches(Rule, host.Sample.IsSelf ? ForegroundInfo.None : host.Sample.Info);

        if (Rule.Condition == ConditionKind.TimeWindow)
        {
            var inWin = RuleEngine.InWindow(Rule, now);
            Progress = inWin ? 1 : 0;
            Right = inWin ? $"Сейчас запрещено, до {Format.Hm(Rule.WindowEndMinutes)}" : $"Запрет начнётся через {Format.Duration(host.Engine.Remaining(Rule, now).TotalSeconds)}";
        }
        else
        {
            var used = host.Engine.UsedSeconds(Rule, now);
            Progress = Math.Clamp(used / (Rule.LimitMinutes * 60.0), 0, 1);
            Right = $"Осталось: {Format.Duration(host.Engine.Remaining(Rule, now).TotalSeconds)}";
        }
        ProgressBrush = Progress > 0.85 ? Hot : Accent;

        (State, StateBg, StateFg, Order) = (Rule.Enabled, running, st.Phase) switch
        {
            (false, _, _) => ("выключено", Visuals.Hex("#1E2346"), Visuals.Hex("#7D83B0"), 9),
            (_, false, _) => ("пауза", Visuals.Hex("#3B2C1E"), Visuals.Hex("#FFB648"), 8),
            (_, _, RulePhase.Blocked) => ("заблокировано", Visuals.Hex("#3D1830"), Visuals.Hex("#FF6F89"), 1),
            (_, _, RulePhase.Warning) => ("лимит!", Visuals.Hex("#3D1830"), Visuals.Hex("#FF6F89"), 0),
            (_, _, RulePhase.Snoozed) => ("отсрочка", Visuals.Hex("#3B2C1E"), Visuals.Hex("#FFB648"), 0),
            _ when active => ("идёт учёт", Visuals.Hex("#163D36"), Visuals.Hex("#34E39A"), 2),
            _ => ("ожидает", Visuals.Hex("#1E2346"), Visuals.Hex("#9FA8FF"), 5),
        };

        if (Rule.Enabled && running)
        {
            switch (st.Phase)
            {
                case RulePhase.Blocked:
                    Progress = 1;
                    ProgressBrush = RedB;
                    Right = $"Заблокировано {Format.Until(st.Until ?? now, now)}";
                    break;
                case RulePhase.Warning:
                    Right = $"Закроется через {Format.Countdown((st.Until ?? now) - now)}";
                    break;
                case RulePhase.Snoozed:
                    Right = $"Отсрочка: закроется через {Format.Countdown((st.Until ?? now) - now)}";
                    break;
            }
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}

public partial class MainWindow
{
    private readonly AppHost _host;
    private List<RuleRow> _rows = new();
    private string _page = "home";
    private bool _loadingSettings;
    private DateTime _weakenApprovedAt = DateTime.MinValue;
    private static bool _trayHintShown;

    public MainWindow(AppHost host)
    {
        InitializeComponent();
        _host = host;
        Icon = Dialogs.AppIcon;
        if (Services.TestMode.Offscreen)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = Services.TestMode.OffscreenLeft;
            ShowActivated = false;
        }

        SourceInitialized += (_, _) =>
        {
            // Скруглённые углы Windows 11 для окна без системной рамки.
            var h = new WindowInteropHelper(this).Handle;
            var pref = 2;
            DwmSetWindowAttribute(h, 33, ref pref, sizeof(int));
        };
        StateChanged += (_, _) =>
        {
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        };

        host.Ticked += OnTick;
        host.Changed += OnChanged;
        Closed += (_, _) =>
        {
            host.Ticked -= OnTick;
            host.Changed -= OnChanged;
        };

        InitSettings();
        BuildTeam();
        OnChanged();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ================= Навигация =================

    public void Navigate(string page)
    {
        (page switch { "rules" => NavRules, "stats" => NavStats, "settings" => NavSettings, _ => NavHome }).IsChecked = true;
    }

    private void NavChecked(object sender, RoutedEventArgs e)
    {
        if (PageHome is null) return;
        _page = sender == NavRules ? "rules" : sender == NavStats ? "stats" : sender == NavSettings ? "settings" : "home";
        PageHome.Visibility = _page == "home" ? Visibility.Visible : Visibility.Collapsed;
        PageRules.Visibility = _page == "rules" ? Visibility.Visible : Visibility.Collapsed;
        PageStats.Visibility = _page == "stats" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = _page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        (PageTitle.Text, PageSubtitle.Text) = _page switch
        {
            "rules" => ("Ограничения", "Приложения и сайты, которые SelfLock держит под контролем"),
            "stats" => ("Статистика", "Сколько времени уходит и куда — всё считается только на этом компьютере"),
            "settings" => ("Настройки", "Как SelfLock предупреждает, закрывает и защищает ваши решения"),
            _ => ("Главная", DateTime.Now.ToString("dddd, d MMMM", Ru.Culture)),
        };
        PageHome.ScrollToTop();
        PageStats.ScrollToTop();
        PageSettings.ScrollToTop();
        if (_page == "stats") BuildStats();
        if (_page == "settings") LoadSettings();
        RefreshLive();
    }

    // ================= Обновление =================

    private void OnChanged()
    {
        _rows = _host.Store.Rules.Select(r => new RuleRow(r)).ToList();
        ApplyFilter();
        RefreshLive();
        if (_page == "stats") BuildStats();
        if (_page == "settings") LoadSettings();
    }

    private long _tick;

    private void OnTick()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        _tick++;
        RefreshLive();
        if (_page == "stats" && _tick % 5 == 0) BuildStats();
        if (_page == "settings" && _tick % 5 == 0) PermList.ItemsSource = PermissionsWindow.Check();
    }

    private void RefreshLive()
    {
        var now = DateTime.Now;
        foreach (var r in _rows) r.Update(_host, now);

        var home = _rows.Where(r => r.Rule.Enabled).OrderBy(r => r.Order).ThenByDescending(r => r.Progress).Take(4).ToList();
        if (!home.SequenceEqual(HomeRules.ItemsSource as IEnumerable<RuleRow> ?? [])) HomeRules.ItemsSource = home;
        HomeEmpty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeRules.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        UpdateStatus(now);
        if (_page == "home") UpdateHome(now);
    }

    private void UpdateStatus(DateTime now)
    {
        var s = _host.Store.Settings;
        string title, text, glyph, color;
        string? action = null;
        if (!s.PermissionsAccepted)
            (title, text, glyph, color, action) = ("Нужны разрешения", "Правила не включаются, пока вы не разрешите SelfLock видеть активное окно и закрывать ресурсы.", "", "#FFB648", "Разрешить");
        else if (!s.Active)
            (title, text, glyph, color) = ("SelfLock выключен", "Ограничения не работают. Включите защиту переключателем справа.", "", "#FF4D6D");
        else if (_host.Engine.IsPaused(now))
            (title, text, glyph, color, action) = ("Пауза", $"Ограничения приостановлены до {s.PausedUntil:HH:mm}. Учёт времени продолжается.", "", "#FFB648", "Возобновить");
        else
        {
            var blocked = _host.Store.Rules.Count(r => r.Enabled && _host.Engine.StateOf(r).Phase == RulePhase.Blocked);
            var enabled = _host.Store.Rules.Count(r => r.Enabled);
            (title, text, glyph, color) = ("SelfLock активен",
                enabled == 0 ? "Защита включена. Добавьте первое ограничение — и SelfLock начнёт следить за временем."
                : blocked > 0 ? $"Работает {enabled} {Format.Plural(enabled, "правило", "правила", "правил")}, заблокировано сейчас: {blocked}. Так держать."
                : $"Работает {enabled} {Format.Plural(enabled, "правило", "правила", "правил")}. Все ограничения на страже вашего времени.", "", "#34E39A");
        }

        var brush = Visuals.Hex(color);
        StatusTitle.Text = title;
        StatusTitle.Foreground = brush;
        StatusText.Text = text;
        StatusGlyph.Text = glyph;
        StatusShield.Fill = brush;
        StatusGlow.Color = (Color)ColorConverter.ConvertFromString(color);
        StatusAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        StatusAction.Content = action;
        ActiveToggle.IsChecked = s.Active;
        ActiveToggle.IsEnabled = s.PermissionsAccepted;

        StatusDot.Fill = brush;
        ((DropShadowEffect)StatusDot.Effect).Color = (Color)ColorConverter.ConvertFromString(color);
        StatusLine.Text = title == "SelfLock активен" ? "Защита активна" : title;

        var paused = _host.Engine.IsPaused(now) || !s.Active;
        PauseText.Text = paused ? "Возобновить защиту" : "Пауза на 1 час";
        PauseGlyph.Text = paused ? "" : "";
    }

    private void UpdateHome(DateTime now)
    {
        var usage = _host.Store.Usage;
        var total = usage.DayTotal(now);
        var limited = Math.Min(total, usage.RuleDayTotal(now));
        TodayDate.Text = now.ToString("d MMMM yyyy", Ru.Culture);
        TodayRing.Value = total > 0 ? limited / total : 0;
        TodayLimited.Text = Format.Duration(limited);
        TodayTotal.Text = $"из {Format.Duration(total)}\nза компьютером";
        var today = usage.Triggers.Where(t => t.At.Date == now.Date).ToList();
        TodayTriggers.Text = today.Count(t => t.Action is "closed" or "hard").ToString();
        TodaySnoozes.Text = today.Count(t => t.Action == "snooze").ToString();

        // Ближайшее ограничение: сначала то, что уже горит, потом — с наименьшим остатком.
        var next = _rows.Where(r => r.Rule.Enabled)
            .OrderBy(r => r.Order)
            .ThenBy(r => _host.Engine.Remaining(r.Rule, now))
            .FirstOrDefault();
        if (next is null || !_host.Engine.IsRunning(now))
        {
            NextBadge.Look = new Look(null, Visuals.Hex("#1E2346"), "—");
            NextName.Text = next is null ? "Нет активных правил" : "Ограничения на паузе";
            NextText.Text = next is null ? "Добавьте первое ограничение" : "Ничего не закроется, пока пауза";
        }
        else
        {
            var st = _host.Engine.StateOf(next.Rule);
            NextBadge.Look = next.Look;
            NextName.Text = next.Name;
            NextText.Text = st.Phase switch
            {
                RulePhase.Blocked => $"Заблокировано {Format.Until(st.Until ?? now, now)}",
                RulePhase.Snoozed => $"Отсрочка: закроется в {st.Until:HH:mm}",
                RulePhase.Warning => "Лимит достигнут — ждём вашего решения",
                _ when next.Rule.Condition == ConditionKind.TimeWindow => RuleEngine.InWindow(next.Rule, now)
                    ? $"Запрещено до {Format.Hm(next.Rule.WindowEndMinutes)}"
                    : $"Запрет с {Format.Hm(next.Rule.WindowStartMinutes)} (через {Format.Duration(_host.Engine.Remaining(next.Rule, now).TotalSeconds)})",
                _ => $"Закроется через {Format.Duration(_host.Engine.Remaining(next.Rule, now).TotalSeconds)} ({Format.Condition(next.Rule)})",
            };
        }

        // Что сейчас на экране и считается ли это каким-то правилом.
        var sample = _host.Sample;
        if (sample.IsSelf || sample.Info.ProcessName.Length == 0)
        {
            NowBadge.Look = Visuals.ForApp("selflock", Environment.ProcessPath);
            NowName.Text = "SelfLock";
            NowText.Text = "Переключитесь на другое окно — здесь появится, что учитывается";
        }
        else
        {
            var key = Matcher.UsageKey(sample.Info);
            NowBadge.Look = Visuals.ForUsageKey(key, usage.Apps);
            NowName.Text = Visuals.NameForUsageKey(key, usage.Apps);
            var rule = _host.Store.Rules.FirstOrDefault(r => r.Enabled && Matcher.Matches(r, sample.Info));
            NowText.Text = rule is not null ? $"Учитывается правилом «{rule.Name}»"
                : sample.Info.IsBrowser && sample.Info.Url is null ? "Браузер: адрес пока не прочитан"
                : "Не ограничивается";
        }
    }

    // ================= Правила =================

    private void ApplyFilter()
    {
        var list = _rows.AsEnumerable();
        if (FilterApps.IsChecked == true) list = list.Where(r => r.Rule.Kind == TargetKind.App);
        if (FilterSites.IsChecked == true) list = list.Where(r => r.Rule.Kind == TargetKind.Site);
        var items = list.ToList();
        RulesList.ItemsSource = items;
        RulesEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (RulesList is not null) ApplyFilter();
    }

    private static Rule RuleOf(object sender) => ((RuleRow)((FrameworkElement)sender).Tag).Rule;

    private void ToggleRule(object sender, RoutedEventArgs e)
    {
        var cb = (CheckBox)sender;
        _host.SetRuleEnabled(this, RuleOf(sender), cb.IsChecked == true);
    }

    private void EditRule(object sender, RoutedEventArgs e)
    {
        var rule = RuleOf(sender);
        if (_host.LockReason(rule) is { } why)
        {
            MessageWindow.Show(this, "Правило заблокировано", why);
            return;
        }
        RuleEditorWindow.Open(_host, this, rule, null);
    }

    private void DeleteRule(object sender, RoutedEventArgs e) => _host.DeleteRule(this, RuleOf(sender));

    private void AddRule(object sender, RoutedEventArgs e) => RuleEditorWindow.Open(_host, this, null, null);

    private void QuickShorts(object sender, RoutedEventArgs e) =>
        _host.AddRule(new Rule { Name = "YouTube Shorts", Kind = TargetKind.Site, Target = "youtube.com/shorts", LimitMinutes = 15, Mode = ReactionMode.Hard, BlockMinutes = 60 });

    private void QuickYouTube(object sender, RoutedEventArgs e) =>
        _host.AddRule(new Rule { Name = "YouTube", Kind = TargetKind.Site, Target = "youtube.com", LimitMinutes = 30, SnoozeMinutes = 5, BlockMinutes = 30 });

    private void OpenRules(object sender, RoutedEventArgs e) => Navigate("rules");
    private void OpenStats(object sender, RoutedEventArgs e) => Navigate("stats");

    // ================= Статус и пауза =================

    private void ActiveToggleClick(object sender, RoutedEventArgs e) => _host.SetActive(this, ActiveToggle.IsChecked == true);

    private void StatusActionClick(object sender, RoutedEventArgs e)
    {
        if (!_host.Store.Settings.PermissionsAccepted) PermissionsWindow.Ask(this);
        else _host.Resume();
    }

    private void PauseClick(object sender, RoutedEventArgs e)
    {
        var s = _host.Store.Settings;
        if (_host.Engine.IsPaused(DateTime.Now) || !s.Active) _host.Resume();
        else _host.Pause(this, TimeSpan.FromHours(1));
    }

    // ================= Статистика =================

    private void RangeChanged(object sender, RoutedEventArgs e)
    {
        if (Chart is not null) BuildStats();
    }

    private void BuildStats()
    {
        var usage = _host.Store.Usage;
        var days = Range30.IsChecked == true ? 30 : 7;
        var today = DateTime.Today;
        var dates = Enumerable.Range(0, days).Select(i => today.AddDays(i - days + 1)).ToList();
        RangeText.Text = $"{dates[0]:dd.MM.yyyy} – {today:dd.MM.yyyy}";

        var totals = dates.Select(d => usage.DayTotal(d)).ToList();
        var limited = dates.Select((d, i) => Math.Min(totals[i], usage.RuleDayTotal(d))).ToList();
        var triggers = usage.Triggers.Where(t => t.At.Date >= dates[0]).ToList();

        Tiles.Children.Clear();
        AddTile("", "За компьютером", Format.Duration(totals.Sum()), $"в среднем {Format.Duration(totals.Sum() / days)} в день");
        AddTile("", "В ограничиваемых", Format.Duration(limited.Sum()), totals.Sum() > 0 ? $"{limited.Sum() / totals.Sum():P0} всего времени" : "пока нет данных");
        AddTile("", "Закрыто SelfLock", triggers.Count(t => t.Action is "closed" or "hard").ToString(), "раз ресурс был закрыт");
        AddTile("", "Отсрочек", triggers.Count(t => t.Action == "snooze").ToString(), "использовано отсрочек");

        BuildChart(dates, totals, limited);

        // Топ ресурсов за период
        var sums = new Dictionary<string, double>();
        foreach (var d in dates)
            foreach (var (k, v) in usage.Day(d))
                sums[k] = sums.GetValueOrDefault(k) + v;
        var sum = sums.Values.Sum();
        TopList.Children.Clear();
        foreach (var (k, v) in sums.OrderByDescending(p => p.Value).Take(6))
            TopList.Children.Add(TopRow(Visuals.ForUsageKey(k, usage.Apps), Visuals.NameForUsageKey(k, usage.Apps), v, sum > 0 ? v / sum : 0));
        if (sums.Count == 0) TopList.Children.Add(new TextBlock { Style = (Style)FindResource("Sub"), Text = "Данные появятся, как только вы поработаете за компьютером с запущенным SelfLock." });

        EventsList.Children.Clear();
        foreach (var t in usage.Triggers.AsEnumerable().Reverse().Take(8))
            EventsList.Children.Add(EventRow(t));
        if (usage.Triggers.Count == 0) EventsList.Children.Add(new TextBlock { Style = (Style)FindResource("Sub"), Text = "Пока ни одного срабатывания. Это либо отличная дисциплина, либо правила ещё впереди." });
    }

    private void AddTile(string glyph, string title, string value, string sub)
    {
        var b = new Border { Style = (Style)FindResource("CardBorder"), Margin = new Thickness(Tiles.Children.Count == 0 ? 0 : 8, 0, Tiles.Children.Count == 3 ? 0 : 8, 0), Padding = new Thickness(18, 16, 18, 16) };
        var sp = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Style = (Style)FindResource("Icon"), Text = glyph, FontSize = 14, Foreground = Visuals.Hex("#9FA8FF"), Margin = new Thickness(0, 0, 8, 0) });
        head.Children.Add(new TextBlock { Text = title, Foreground = (Brush)FindResource("Muted"), FontSize = 12.5 });
        sp.Children.Add(head);
        sp.Children.Add(new TextBlock { Text = value, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) });
        sp.Children.Add(new TextBlock { Text = sub, Style = (Style)FindResource("Sub"), FontSize = 11.5 });
        b.Child = sp;
        Tiles.Children.Add(b);
    }

    private void BuildChart(List<DateTime> dates, List<double> totals, List<double> limited)
    {
        const double h = 196;
        Chart.Children.Clear();
        Chart.ColumnDefinitions.Clear();
        Chart.RowDefinitions.Clear();
        Chart.RowDefinitions.Add(new RowDefinition { Height = new GridLength(h) });
        Chart.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Chart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        foreach (var _ in dates) Chart.ColumnDefinitions.Add(new ColumnDefinition());

        // Шкала: ровные часы (или минуты, если времени мало).
        var max = Math.Max(totals.DefaultIfEmpty(0).Max(), 1);
        var stepSec = max <= 3600 ? 15 * 60.0 : Math.Ceiling(max / 3 / 3600) * 3600;
        var top = stepSec * Math.Max(1, Math.Ceiling(max / stepSec));
        var lines = (int)(top / stepSec);

        for (var i = 0; i <= lines; i++)
        {
            var y = h - i * h / lines;
            var line = new Border { Height = 1, Background = (Brush)FindResource("StrokeSoft"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, y, 0, 0) };
            Grid.SetColumn(line, 1);
            Grid.SetColumnSpan(line, dates.Count);
            Chart.Children.Add(line);
            var lbl = new TextBlock { Text = i == 0 ? "0" : stepSec < 3600 ? $"{i * stepSec / 60:0} м" : $"{i * stepSec / 3600:0} ч", FontSize = 11, Foreground = (Brush)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, Math.Max(0, y - 8), 0, 0) };
            Chart.Children.Add(lbl);
        }

        var barWidth = dates.Count > 10 ? 12 : 30;
        var accent = (Brush)FindResource("AccentGrad");
        for (var i = 0; i < dates.Count; i++)
        {
            var cell = new Grid { ToolTip = $"{dates[i]:dd.MM, ddd}: всего {Format.Duration(totals[i])}, в ограничиваемых {Format.Duration(limited[i])}", Background = Brushes.Transparent };
            var totalBar = new Border
            {
                Width = barWidth, Height = Math.Max(2, totals[i] / top * h), VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(6, 6, 2, 2), Background = Visuals.Hex("#2B3260"),
            };
            var limBar = new Border
            {
                Width = barWidth, Height = limited[i] / top * h, VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(6, 6, 2, 2), Background = accent,
            };
            if (dates[i] == DateTime.Today)
            {
                totalBar.Background = Visuals.Hex("#3A4384");
                limBar.Effect = new DropShadowEffect { Color = (Color)ColorConverter.ConvertFromString("#7C5CFF"), BlurRadius = 18, ShadowDepth = 0, Opacity = 0.8 };
            }
            cell.Children.Add(totalBar);
            cell.Children.Add(limBar);
            Grid.SetColumn(cell, i + 1);
            Chart.Children.Add(cell);

            var showLabel = dates.Count <= 10 || i % 5 == 4 || i == dates.Count - 1;
            if (showLabel)
            {
                var lbl = new TextBlock
                {
                    Text = dates.Count <= 10 ? dates[i].ToString("ddd dd.MM", Ru.Culture) : dates[i].ToString("dd.MM"),
                    FontSize = 11, Foreground = dates[i] == DateTime.Today ? (Brush)FindResource("Text") : (Brush)FindResource("Dim"),
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0),
                };
                Grid.SetColumn(lbl, i + 1);
                Grid.SetRow(lbl, 1);
                Chart.Children.Add(lbl);
            }
        }
    }

    private FrameworkElement TopRow(Look look, string name, double seconds, double share)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        var badge = new ResourceBadge { Width = 32, Height = 32, CornerRadius = new CornerRadius(9), Look = look };
        var n = new TextBlock { Text = name, Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var d = new TextBlock { Text = Format.Duration(seconds), Foreground = Visuals.Hex("#C9CDF2"), VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
        var bar = new ProgressBar { Style = (Style)FindResource("Bar"), Value = share, VerticalAlignment = VerticalAlignment.Center };
        var p = new TextBlock { Text = $"{share:P0}", Foreground = (Brush)FindResource("Muted"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
        Grid.SetColumn(n, 1); Grid.SetColumn(d, 2); Grid.SetColumn(bar, 3); Grid.SetColumn(p, 4);
        g.Children.Add(badge); g.Children.Add(n); g.Children.Add(d); g.Children.Add(bar); g.Children.Add(p);
        return g;
    }

    private FrameworkElement EventRow(TriggerRecord t)
    {
        var (text, color) = t.Action switch
        {
            "warning" => ("предупреждение", "#FFB648"),
            "snooze" => ("отсрочка", "#B9A8FF"),
            "hard" => ("закрыто сразу", "#FF4D6D"),
            _ => ("закрыто", "#FF4D6D"),
        };
        var dp = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };
        var time = new TextBlock { Text = t.At.Date == DateTime.Today ? t.At.ToString("HH:mm") : t.At.ToString("dd.MM HH:mm"), Foreground = (Brush)FindResource("Dim"), FontSize = 12, Width = 78, VerticalAlignment = VerticalAlignment.Center };
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Visuals.Hex(color), VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = t.RuleName, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
        var act = new TextBlock { Text = text, Foreground = Visuals.Hex(color), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(act, Dock.Right);
        dp.Children.Add(time); dp.Children.Add(act); dp.Children.Add(dot); dp.Children.Add(name);
        return dp;
    }

    // ================= Настройки =================

    private void InitSettings()
    {
        SetPreWarn.Minimum = 0; SetPreWarn.Maximum = 30; SetPreWarn.Formatter = v => v == 0 ? "не напоминать" : $"за {v} мин";
        SetWarnSeconds.Minimum = 10; SetWarnSeconds.Maximum = 120; SetWarnSeconds.Step = 5; SetWarnSeconds.Unit = "сек";
        SetReset.Minimum = 1; SetReset.Maximum = 60; SetReset.Unit = "мин";
        SetDelay.Minimum = 0; SetDelay.Maximum = 300; SetDelay.Step = 5; SetDelay.Formatter = v => v == 0 ? "без задержки" : $"{v} сек";
        foreach (var s in new[] { SetPreWarn, SetWarnSeconds, SetReset, SetDelay })
            s.ValueChanged += _ => SettingStepped(s);
        DataPath.Text = _host.Store.Directory;
        LoadSettings();
    }

    private void LoadSettings()
    {
        _loadingSettings = true;
        var s = _host.Store.Settings;
        SetAutoStart.IsChecked = s.AutoStart;
        SetTray.IsChecked = s.MinimizeToTray;
        SetNotify.IsChecked = s.ShowNotifications;
        SetLock.IsChecked = s.LockRulesWhileBlocked;
        SetPreWarn.Value = s.PreWarnMinutes;
        SetWarnSeconds.Value = s.WarningSeconds;
        SetReset.Value = s.SessionResetMinutes;
        SetDelay.Value = s.ProtectionDelaySeconds;
        PermList.ItemsSource = PermissionsWindow.Check();
        _loadingSettings = false;
    }

    /// <summary>Ослабление защиты в настройках подтверждается один раз, дальше пару минут можно крутить без вопросов.</summary>
    private bool ApproveWeakening(string what)
    {
        if (DateTime.Now - _weakenApprovedAt < TimeSpan.FromMinutes(2)) return true;
        if (!_host.ConfirmWeakening(this, "Ослабить защиту?", what, "Подтвердить")) return false;
        _weakenApprovedAt = DateTime.Now;
        return true;
    }

    private void SettingStepped(Stepper s)
    {
        if (_loadingSettings) return;
        var st = _host.Store.Settings;
        if (s == SetDelay && s.Value < st.ProtectionDelaySeconds && !ApproveWeakening("Вы уменьшаете задержку подтверждения — так отключить ограничения станет проще."))
        {
            _loadingSettings = true;
            s.Value = st.ProtectionDelaySeconds;
            _loadingSettings = false;
            return;
        }
        st.PreWarnMinutes = SetPreWarn.Value;
        st.WarningSeconds = SetWarnSeconds.Value;
        st.SessionResetMinutes = SetReset.Value;
        st.ProtectionDelaySeconds = SetDelay.Value;
        _host.Store.SaveSettings();
    }

    private void SettingClick(object sender, RoutedEventArgs e)
    {
        var st = _host.Store.Settings;
        if (sender == SetLock && SetLock.IsChecked != true && !ApproveWeakening("Правила можно будет менять даже во время блокировки."))
        {
            SetLock.IsChecked = true;
            return;
        }
        if (sender == SetAutoStart)
        {
            try { Services.AutoStart.Set(SetAutoStart.IsChecked == true); }
            catch (Exception ex) { App.Log(ex); }
            st.AutoStart = Services.AutoStart.IsEnabled();
            SetAutoStart.IsChecked = st.AutoStart;
        }
        st.MinimizeToTray = SetTray.IsChecked == true;
        st.ShowNotifications = SetNotify.IsChecked == true;
        st.LockRulesWhileBlocked = SetLock.IsChecked == true;
        _host.SaveSettings();
    }

    private void OpenPermissions(object sender, RoutedEventArgs e)
    {
        PermissionsWindow.Ask(this);
        PermList.ItemsSource = PermissionsWindow.Check();
    }

    private void OpenData(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_host.Store.Directory}\"") { UseShellExecute = true });

    private void BuildTeam()
    {
        (string Name, string Role)[] team =
        [
            ("Жуков Сергей", "TeamLead (Scrum Master)"),
            ("Дудоров Константин", "Frontend-разработчик"),
            ("Панин Илья", "QA-тестирование"),
            ("Колосов Данил", "UI/UX-дизайнер"),
        ];
        TeamGrid.ColumnDefinitions.Add(new ColumnDefinition());
        TeamGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < team.Length; i++)
        {
            TeamGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var n = new TextBlock { Text = team[i].Name, FontSize = 13, Margin = new Thickness(0, 3, 0, 3) };
            var r = new TextBlock { Text = team[i].Role, Foreground = (Brush)FindResource("Muted"), FontSize = 12.5, Margin = new Thickness(0, 3, 0, 3) };
            Grid.SetRow(n, i); Grid.SetRow(r, i); Grid.SetColumn(r, 1);
            TeamGrid.Children.Add(n); TeamGrid.Children.Add(r);
        }
    }

    // ================= Окно =================

    private void Minimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_host.IsExiting) return;
        e.Cancel = true;
        if (_host.Store.Settings.MinimizeToTray)
        {
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                ToastWindow.Show("SelfLock работает в фоне", "Окно спрятано в трей возле часов. Ограничения продолжают действовать.", "");
            }
        }
        else _host.RequestExit();
    }
}

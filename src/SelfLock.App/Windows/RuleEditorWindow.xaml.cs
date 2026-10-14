using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SelfLock.Core;
using SelfLock.Services;
using SelfLock.UI;

namespace SelfLock.Windows;

public sealed record AppItem(string Process, string Name, string? Path)
{
    public Look Look => Visuals.ForApp(Process, Path);
}

/// <summary>Создание и редактирование правила: ресурс + условие + режим реакции.</summary>
public partial class RuleEditorWindow
{
    private readonly AppHost _host;
    private readonly Rule? _original;
    private List<AppItem> _apps = new();
    private string? _exePath;
    private string _autoName = "";
    private bool _ready;

    private RuleEditorWindow(AppHost host, Rule? original, TargetKind? kind)
    {
        InitializeComponent();
        _host = host;
        _original = original;
        Icon = Dialogs.AppIcon;
        MaxHeight = SystemParameters.WorkArea.Height - 20;

        LimitStepper.Minimum = 1; LimitStepper.Maximum = 720;
        SnoozeStepper.Minimum = 1; SnoozeStepper.Maximum = 60;
        BlockStepper.Minimum = 5; BlockStepper.Maximum = 720;
        foreach (var s in new[] { LimitStepper, BlockStepper })
            s.Formatter = v => Format.Duration(v * 60);
        foreach (var s in new[] { FromStepper, ToStepper })
        {
            s.Minimum = 0; s.Maximum = 24 * 60 - 15; s.Step = 15; s.MinWidth = 120;
            s.Formatter = Format.Hm;
        }
        foreach (var s in new[] { LimitStepper, SnoozeStepper, BlockStepper, FromStepper, ToStepper })
            s.ValueChanged += _ => UpdateSummary();
        ChoosableBox.Checked += (_, _) => UpdateSummary();
        ChoosableBox.Unchecked += (_, _) => UpdateSummary();
        NameBox.TextChanged += (_, _) => UpdateSummary();

        foreach (var p in Visuals.Sites)
        {
            var chip = new Button { Style = (Style)FindResource("Chip"), Tag = p };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new ResourceBadge { Width = 20, Height = 20, CornerRadius = new CornerRadius(6), Look = Visuals.ForSite(p.Pattern) });
            sp.Children.Add(new TextBlock { Text = p.Name, Margin = new Thickness(8, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center });
            chip.Content = sp;
            chip.Click += (_, _) => PickPreset(p);
            Presets.Children.Add(chip);
        }

        LoadApps();

        var r = original ?? new Rule { Kind = kind ?? TargetKind.Site };
        HeaderText.Text = original is null ? "Добавить ограничение" : "Изменить ограничение";
        SaveButton.Content = original is null ? "Добавить" : "Сохранить";

        LimitStepper.Value = r.LimitMinutes;
        SnoozeStepper.Value = r.SnoozeMinutes;
        BlockStepper.Value = r.BlockMinutes;
        FromStepper.Value = r.WindowStartMinutes;
        ToStepper.Value = r.WindowEndMinutes;
        ChoosableBox.IsChecked = r.SnoozeChoosable;
        (r.Kind == TargetKind.App ? KindApp : KindSite).IsChecked = true;
        (r.Condition switch { ConditionKind.DailyTotal => CondDaily, ConditionKind.TimeWindow => CondWindow, _ => CondContinuous }).IsChecked = true;
        (r.Mode == ReactionMode.Hard ? ModeHard : ModeSoft).IsChecked = true;

        if (original is not null)
        {
            if (r.Kind == TargetKind.App)
            {
                _exePath = r.ExePath;
                ProcessBox.Text = r.Target;
                AppList.SelectedItem = _apps.FirstOrDefault(a => a.Process == r.Target);
            }
            else SiteBox.Text = r.Target;
            NameBox.Text = r.Name;
            _autoName = "";
        }

        _ready = true;
        Sync();
    }

    public static void Open(AppHost host, Window? owner, Rule? rule, TargetKind? kind)
    {
        var w = new RuleEditorWindow(host, rule, kind);
        Dialogs.Place(w, owner);
        w.ShowDialog();
    }

    private void LoadApps()
    {
        _apps = AppCatalog.Running(_host.Store.Usage.Apps)
            .Select(a => new AppItem(a.Process, a.Name, a.Path)).ToList();
        FilterApps();
    }

    private void FilterApps()
    {
        var q = AppSearch.Text.Trim();
        AppList.ItemsSource = q.Length == 0
            ? _apps
            : _apps.Where(a => a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || a.Process.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // ===== Выбор ресурса =====

    private void AppSearchChanged(object sender, TextChangedEventArgs e) => FilterApps();

    private void AppSelected(object sender, SelectionChangedEventArgs e)
    {
        if (AppList.SelectedItem is not AppItem a) return;
        _exePath = a.Path;
        ProcessBox.Text = a.Process;
        SetAutoName(a.Name);
    }

    private void ProcessTyped(object sender, TextChangedEventArgs e)
    {
        if (AppList.SelectedItem is AppItem a && a.Process != Matcher.NormalizeProcess(ProcessBox.Text))
        {
            AppList.SelectedItem = null;
            _exePath = null;
        }
        if (AppList.SelectedItem is null && ProcessBox.Text.Trim().Length > 0)
            SetAutoName(AppCatalog.DisplayName(Matcher.NormalizeProcess(ProcessBox.Text), null));
        UpdateSummary();
    }

    private void Browse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Программы (*.exe)|*.exe", Title = "Какое приложение ограничить?" };
        if (dlg.ShowDialog(this) != true) return;
        var proc = Path.GetFileNameWithoutExtension(dlg.FileName).ToLowerInvariant();
        var item = _apps.FirstOrDefault(a => a.Process == proc);
        if (item is null)
        {
            item = new AppItem(proc, AppCatalog.DisplayName(proc, dlg.FileName), dlg.FileName);
            _apps.Insert(0, item);
            AppSearch.Text = "";
            FilterApps();
        }
        AppList.SelectedItem = item;
        AppList.ScrollIntoView(item);
    }

    private void PickPreset(SitePreset p)
    {
        SiteBox.Text = p.Pattern;
        SetAutoName(p.Name);
        if (_original is null && p.HardByDefault) ModeHard.IsChecked = true;
    }

    private void SiteTyped(object sender, TextChangedEventArgs e)
    {
        var t = SiteBox.Text.Trim();
        if (t.Length > 0)
        {
            var p = Visuals.SitePresetFor(t);
            SetAutoName(p is not null && Matcher.NormalizeUrl(t) == p.Pattern ? p.Name : Matcher.SplitUrl(Matcher.NormalizeUrl(t)).Host);
        }
        UpdateSummary();
    }

    /// <summary>Подставляет название, если пользователь не вписал своё.</summary>
    private void SetAutoName(string name)
    {
        if (NameBox.Text.Length == 0 || NameBox.Text == _autoName)
        {
            _autoName = name;
            NameBox.Text = name;
        }
    }

    // ===== Переключатели =====

    private void KindChanged(object sender, RoutedEventArgs e) => Sync();
    private void CondChanged(object sender, RoutedEventArgs e) => Sync();
    private void ModeChanged(object sender, RoutedEventArgs e) => Sync();

    private void Sync()
    {
        if (!_ready) return;
        var app = KindApp.IsChecked == true;
        AppPanel.Visibility = app ? Visibility.Visible : Visibility.Collapsed;
        SitePanel.Visibility = app ? Visibility.Collapsed : Visibility.Visible;

        var cond = Condition;
        LimitRow.Visibility = cond == ConditionKind.TimeWindow ? Visibility.Collapsed : Visibility.Visible;
        WindowRow.Visibility = cond == ConditionKind.TimeWindow ? Visibility.Visible : Visibility.Collapsed;
        BlockRow.Visibility = cond == ConditionKind.Continuous ? Visibility.Visible : Visibility.Collapsed;
        CondHint.Text = cond switch
        {
            ConditionKind.Continuous => $"Время без перерыва. Перерыв дольше {_host.Store.Settings.SessionResetMinutes} мин начинает отсчёт заново.",
            ConditionKind.DailyTotal => "Суммарное время за сутки. В полночь счётчик обнуляется.",
            _ => "Ресурс недоступен в выбранные часы. Окно может проходить через полночь (например, 22:00–07:00).",
        };
        BlockHint.Text = cond switch
        {
            ConditionKind.DailyTotal => "После срабатывания ресурс заблокирован до конца дня.",
            ConditionKind.TimeWindow => "После срабатывания ресурс заблокирован до конца запрещённого времени.",
            _ => "",
        };
        BlockHint.Visibility = cond == ConditionKind.Continuous ? Visibility.Collapsed : Visibility.Visible;

        var soft = ModeSoft.IsChecked == true;
        SoftPanel.Visibility = soft ? Visibility.Visible : Visibility.Collapsed;
        ModeHint.Text = soft
            ? "Появится предупреждение. Его можно один раз отложить на заранее заданное время — успеть закончить начатое, но без бесконечного «ещё пять минут»."
            : "Закрывается сразу, без вопросов и отсрочек. Подходит для затягивающих лент: YouTube Shorts, TikTok, клипы.";
        UpdateSummary();
    }

    private ConditionKind Condition =>
        CondDaily.IsChecked == true ? ConditionKind.DailyTotal : CondWindow.IsChecked == true ? ConditionKind.TimeWindow : ConditionKind.Continuous;

    private void UpdateSummary()
    {
        if (!_ready) return;
        var r = Build();
        var name = r.Name.Length > 0 ? $"«{r.Name}»" : "выбранном ресурсе";
        var what = r.Kind == TargetKind.App ? "приложение закроется" : "вкладка закроется";
        var when = r.Condition switch
        {
            ConditionKind.Continuous => $"Когда вы проведёте в {name} {Format.Duration(r.LimitMinutes * 60)} подряд",
            ConditionKind.DailyTotal => $"Когда за день в {name} наберётся {Format.Duration(r.LimitMinutes * 60)}",
            _ => $"Если открыть {name} с {Format.Hm(r.WindowStartMinutes)} до {Format.Hm(r.WindowEndMinutes)}",
        };
        var react = r.Mode == ReactionMode.Hard
            ? $", {what} сразу."
            : $", SelfLock предупредит и даст один раз отложить на {(r.SnoozeChoosable ? "до " : "")}{r.SnoozeMinutes} мин. Потом {what}.";
        var after = r.Condition switch
        {
            ConditionKind.Continuous => $" Снова открыть можно будет через {Format.Duration(r.BlockMinutes * 60)}.",
            ConditionKind.DailyTotal => " Снова — завтра.",
            _ => $" Снова — после {Format.Hm(r.WindowEndMinutes)}.",
        };
        SummaryText.Text = when + react + after;
        ErrorText.Text = "";
    }

    private Rule Build()
    {
        var app = KindApp.IsChecked == true;
        var target = app ? Matcher.NormalizeProcess(ProcessBox.Text) : Matcher.NormalizeUrl(SiteBox.Text);
        var r = _original?.Clone() ?? new Rule();
        r.Kind = app ? TargetKind.App : TargetKind.Site;
        r.Target = target;
        r.ExePath = app ? _exePath : null;
        r.Name = NameBox.Text.Trim();
        r.Condition = Condition;
        r.LimitMinutes = LimitStepper.Value;
        r.WindowStartMinutes = FromStepper.Value;
        r.WindowEndMinutes = ToStepper.Value;
        r.Mode = ModeHard.IsChecked == true ? ReactionMode.Hard : ReactionMode.Soft;
        r.SnoozeMinutes = SnoozeStepper.Value;
        r.SnoozeChoosable = ChoosableBox.IsChecked == true;
        r.BlockMinutes = BlockStepper.Value;
        return r;
    }

    private string? Validate(Rule r)
    {
        if (r.Kind == TargetKind.App)
        {
            if (r.Target.Length == 0) return "Выберите приложение из списка или впишите имя процесса.";
            if (Matcher.IsProtected(r.Target)) return "Это системный процесс Windows — его ограничивать нельзя.";
        }
        else
        {
            var host = Matcher.SplitUrl(r.Target).Host;
            if (host.Length == 0) return "Впишите адрес сайта или выберите из популярных.";
            if (!host.Contains('.') || host.Contains(' ')) return "Похоже, это не адрес сайта. Пример: youtube.com";
        }
        if (r.Condition == ConditionKind.TimeWindow && r.WindowStartMinutes == r.WindowEndMinutes)
            return "Начало и конец запрещённого времени совпадают.";
        var dup = _host.Store.Rules.FirstOrDefault(x => x.Id != r.Id && x.Kind == r.Kind && x.Target == r.Target && x.Condition == r.Condition);
        if (dup is not null) return $"Такое правило уже есть: «{dup.Name}».";
        return null;
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        var r = Build();
        if (r.Name.Length == 0) r.Name = r.Kind == TargetKind.App ? AppCatalog.DisplayName(r.Target, r.ExePath) : r.Target;
        if (Validate(r) is { } err)
        {
            ErrorText.Text = err;
            return;
        }

        if (_original is null)
        {
            _host.AddRule(r);
            DialogResult = true;
        }
        else if (_host.UpdateRule(this, _original, r))
        {
            DialogResult = true;
        }
    }

    private void Cancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}

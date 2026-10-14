using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SelfLock.Core;
using SelfLock.Services;
using SelfLock.UI;
using SelfLock.Windows;

namespace SelfLock;

/// <summary>
/// Связывает всё вместе: раз в секунду берёт снимок активного окна, прогоняет движок правил
/// и показывает предупреждения / закрывает ресурсы / обновляет интерфейс и трей.
/// </summary>
public sealed class AppHost
{
    public static AppHost? Current { get; private set; }

    public Store Store { get; }
    public RuleEngine Engine { get; }
    public ForegroundSample Sample { get; private set; } = ForegroundSample.Empty;
    public bool UrlReadWorked => _sampler.UrlReadWorked;

    /// <summary>Каждую секунду — для живого обновления экранов.</summary>
    public event Action? Ticked;
    /// <summary>Список правил или настройки изменились.</summary>
    public event Action? Changed;

    private readonly ForegroundSampler _sampler = new();
    private readonly Enforcer _enforcer = new();
    private readonly Dictionary<string, WarningWindow> _warnings = new();
    private readonly DispatcherTimer _timer;
    private TrayIcon? _tray;
    private MainWindow? _main;
    private BlockWindow? _block;
    private DateTime _lastTick;
    private long _ticks;
    private bool _exiting;

    public AppHost(Store store)
    {
        Current = this;
        Store = store;
        Engine = new RuleEngine(store);
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
    }

    private AppSettings Settings => Store.Settings;

    public void Start(bool startHidden)
    {
        Store.Load();
        Store.Usage.Prune(DateTime.Today);
        Settings.AutoStart = AutoStart.IsEnabled();

        _sampler.Start();
        _tray = new TrayIcon("SelfLock");
        _tray.Open += () => ShowMain();
        _tray.MenuOpening += BuildTrayMenu;

        _lastTick = DateTime.Now;
        _timer.Start();

        if (!startHidden) ShowMain();
        if (!Settings.PermissionsAccepted)
            PermissionsWindow.Ask(_main);
    }

    // ================= Цикл =================

    private void Tick()
    {
        var now = DateTime.Now;
        var delta = (now - _lastTick).TotalSeconds;
        _lastTick = now;
        _ticks++;

        var sample = _sampler.Latest;
        if ((now - sample.At).TotalSeconds > 3) sample = ForegroundSample.Empty;
        Sample = sample;
        var fg = sample.IsSelf ? ForegroundInfo.None : sample.Info;

        if (!sample.IsSelf && fg.ProcessName.Length > 0 && !Store.Usage.Apps.ContainsKey(fg.ProcessName))
            Store.Usage.Apps[fg.ProcessName] = new AppInfo { Name = AppCatalog.DisplayName(fg.ProcessName, sample.Path), Path = sample.Path };

        List<EngineEvent> events;
        try { events = Engine.Tick(now, delta, fg); }
        catch (Exception ex) { App.Log(ex); events = new(); }

        foreach (var e in events) Handle(e, sample);

        // Заблокированные приложения закрываем, даже если они не на переднем плане (например, свернули и запустили снова).
        if (_ticks % 2 == 0 && Engine.IsRunning(now))
        {
            foreach (var r in Store.Rules.Where(r => r.Enabled && r.Kind == TargetKind.App))
                if (Engine.StateOf(r).Phase == RulePhase.Blocked && Enforcer.IsAppRunning(r))
                    _enforcer.CloseApp(r);
        }

        // Предупреждение, которое стало неактуальным (правило выключили, движок поставили на паузу), убираем.
        foreach (var (id, w) in _warnings.ToList())
        {
            var rule = Store.Rules.FirstOrDefault(r => r.Id == id);
            if (rule is null || !rule.Enabled || !Engine.IsRunning(now) || Engine.StateOf(rule).Phase != RulePhase.Warning)
                CloseWarning(id);
            else
                w.Refresh(now);
        }

        _tray?.SetTooltip(TrayText(now));
        Ticked?.Invoke();

        if (_ticks % 15 == 0) SaveRuntime();
    }

    private void Handle(EngineEvent e, ForegroundSample sample)
    {
        var rule = e.Rule;
        switch (e)
        {
            case PreWarnEvent pw when Settings.ShowNotifications:
                ToastWindow.Show("Скоро лимит", $"{rule.Name}: осталось {Format.Duration(pw.Left.TotalSeconds)}. Самое время закончить начатое.", "");
                break;

            case WarningEvent w:
                ShowWarning(rule, w.Deadline, w.CanSnooze);
                break;

            case EnforceEvent en:
                Enforce(rule, en.Fresh, en.Active, sample);
                break;

            case UnblockedEvent when Settings.ShowNotifications:
                ToastWindow.Show("Снова доступно", $"Блокировка «{rule.Name}» закончилась. Пользуйтесь осознанно.", "");
                break;
        }
    }

    private void Enforce(Rule rule, bool fresh, bool active, ForegroundSample sample)
    {
        CloseWarning(rule.Id);
        var closed = rule.Kind == TargetKind.App
            ? _enforcer.CloseApp(rule)
            : active && _enforcer.CloseSite(rule, sample);

        if (closed)
        {
            // Даём браузеру мгновение закрыть вкладку, потом показываем экран блокировки.
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(rule.Kind == TargetKind.Site ? 250 : 50) };
            t.Tick += (_, _) => { t.Stop(); ShowBlock(rule); };
            t.Start();
        }
        else if (fresh)
        {
            var until = Engine.StateOf(rule).Until ?? DateTime.Now;
            ToastWindow.Show("Доступ закрыт", $"«{rule.Name}» заблокирован {Format.Until(until, DateTime.Now)}. Если откроете — SelfLock закроет снова.", "");
        }
    }

    // ================= Окна =================

    public void ShowMain(string? page = null)
    {
        if (_main is null)
        {
            _main = new MainWindow(this);
            _main.Closed += (_, _) => _main = null;
        }
        if (page is not null) _main.Navigate(page);
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        if (!TestMode.Offscreen) _main.Activate();
    }

    public Window? MainWindowOrNull => _main;

    private void ShowWarning(Rule rule, DateTime deadline, bool canSnooze)
    {
        CloseWarning(rule.Id);
        var w = new WarningWindow(this, rule, deadline, canSnooze);
        _warnings[rule.Id] = w;
        w.Closed += (_, _) => { if (_warnings.GetValueOrDefault(rule.Id) == w) _warnings.Remove(rule.Id); };
        w.Show();
    }

    private void CloseWarning(string ruleId)
    {
        if (_warnings.Remove(ruleId, out var w)) w.Close();
    }

    private void ShowBlock(Rule rule)
    {
        var st = Engine.StateOf(rule);
        if (_block is null)
        {
            _block = new BlockWindow();
            _block.Closed += (_, _) => _block = null;
        }
        _block.Set(rule, st, Engine.UsedSeconds(rule, DateTime.Now));
        _block.Show();
        if (!TestMode.Offscreen) _block.Activate();
    }

    // ================= Действия из предупреждения =================

    public void Snooze(Rule rule, int? minutes)
    {
        if (!Engine.Snooze(rule, DateTime.Now, minutes)) return;
        CloseWarning(rule.Id);
        var until = Engine.StateOf(rule).Until!.Value;
        ToastWindow.Show("Отложено", $"«{rule.Name}» закроется в {until:HH:mm}. Отложить ещё раз уже не получится.", "");
        SaveRuntime();
    }

    public void CloseNow(Rule rule)
    {
        if (Engine.CloseNow(rule, DateTime.Now) is not { } e) return;
        var sample = _sampler.Latest;
        Enforce(rule, e.Fresh, Matcher.Matches(rule, sample.Info), sample);
        SaveRuntime();
    }

    // ================= Правила (с защитой от себя) =================

    /// <summary>Нельзя трогать правило, пока оно предупреждает/откладывает/блокирует.</summary>
    public string? LockReason(Rule rule)
    {
        if (!Settings.LockRulesWhileBlocked || !rule.Enabled || !Engine.IsRunning(DateTime.Now)) return null;
        var st = Engine.StateOf(rule);
        return st.Phase switch
        {
            RulePhase.Blocked => $"«{rule.Name}» сейчас заблокирован {Format.Until(st.Until ?? DateTime.Now, DateTime.Now)}. Изменить или удалить правило можно будет после окончания блокировки.",
            RulePhase.Warning or RulePhase.Snoozed => $"По правилу «{rule.Name}» уже идёт отсчёт. Изменить его можно будет после окончания блокировки.",
            _ => null,
        };
    }

    /// <summary>Ослабление защиты подтверждается только после паузы — чтобы решение принималось не «в момент слабости».</summary>
    public bool ConfirmWeakening(Window? owner, string title, string text, string action = "Подтвердить")
    {
        var delay = Store.Rules.Any(r => r.Enabled) ? Settings.ProtectionDelaySeconds : 0;
        return ConfirmWindow.Ask(owner ?? _main, title, text, delay, action);
    }

    public void AddRule(Rule rule)
    {
        Store.Rules.Add(rule);
        Engine.ResetState(rule);
        Store.SaveRules();
        Changed?.Invoke();
    }

    public bool UpdateRule(Window? owner, Rule original, Rule edited)
    {
        if (LockReason(original) is { } why) { MessageWindow.Show(owner, "Правило заблокировано", why); return false; }
        if (original.Enabled && !ConfirmWeakening(owner, "Изменить правило?", $"Вы меняете правило «{original.Name}». Если это попытка ослабить ограничение — может, стоит закончить дело?"))
            return false;
        var i = Store.Rules.IndexOf(original);
        if (i < 0) return false;
        Store.Rules[i] = edited;
        Engine.ResetState(edited);
        Store.SaveRules();
        Changed?.Invoke();
        return true;
    }

    public void DeleteRule(Window? owner, Rule rule)
    {
        if (LockReason(rule) is { } why) { MessageWindow.Show(owner, "Правило заблокировано", why); return; }
        if (!ConfirmWeakening(owner, "Удалить правило?", $"Правило «{rule.Name}» будет удалено вместе с его счётчиками.", "Удалить")) return;
        Store.Rules.Remove(rule);
        Engine.Forget(rule);
        Store.SaveRules();
        Changed?.Invoke();
    }

    public void SetRuleEnabled(Window? owner, Rule rule, bool enabled)
    {
        if (!enabled)
        {
            if (LockReason(rule) is { } why) { MessageWindow.Show(owner, "Правило заблокировано", why); Changed?.Invoke(); return; }
            if (!ConfirmWeakening(owner, "Выключить правило?", $"«{rule.Name}» перестанет ограничиваться.", "Выключить")) { Changed?.Invoke(); return; }
        }
        rule.Enabled = enabled;
        Engine.ResetState(rule);
        Store.SaveRules();
        Changed?.Invoke();
    }

    public void SetActive(Window? owner, bool active)
    {
        if (!active && Settings.Active &&
            !ConfirmWeakening(owner, "Выключить SelfLock?", "Все ограничения перестанут работать, пока вы не включите защиту снова.", "Выключить"))
        {
            Changed?.Invoke();
            return;
        }
        Settings.Active = active;
        if (active) Settings.PausedUntil = null;
        Store.SaveSettings();
        Changed?.Invoke();
    }

    public void Pause(Window? owner, TimeSpan duration)
    {
        if (!ConfirmWeakening(owner, "Приостановить на " + Format.Duration(duration.TotalSeconds) + "?",
                "Пока пауза идёт, SelfLock ничего не ограничивает. Время в приложениях продолжит учитываться в статистике.", "Приостановить"))
            return;
        Settings.PausedUntil = DateTime.Now + duration;
        Store.SaveSettings();
        Changed?.Invoke();
    }

    public void Resume()
    {
        Settings.PausedUntil = null;
        Settings.Active = true;
        Store.SaveSettings();
        Changed?.Invoke();
    }

    public void AcceptPermissions()
    {
        Settings.PermissionsAccepted = true;
        Store.SaveSettings();
        Changed?.Invoke();
    }

    public void SaveSettings()
    {
        Store.SaveSettings();
        Changed?.Invoke();
    }

    public void OpenRuleEditor(Rule? rule = null, TargetKind? kind = null)
    {
        ShowMain();
        RuleEditorWindow.Open(this, _main, rule, kind);
    }

    // ================= Трей =================

    private string TrayText(DateTime now)
    {
        if (!Settings.Active) return "SelfLock — выключен";
        if (!Settings.PermissionsAccepted) return "SelfLock — нужны разрешения";
        if (Engine.IsPaused(now)) return $"SelfLock — пауза до {Settings.PausedUntil:HH:mm}";
        var blocked = Store.Rules.Count(r => r.Enabled && Engine.StateOf(r).Phase == RulePhase.Blocked);
        return blocked > 0 ? $"SelfLock — активен, заблокировано: {blocked}" : "SelfLock — активен";
    }

    private void BuildTrayMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        var now = DateTime.Now;

        var header = new MenuItem { Header = "SelfLock", Tag = "", FontWeight = FontWeights.SemiBold, IsEnabled = true };
        header.Click += (_, _) => ShowMain();
        menu.Items.Add(header);
        menu.Items.Add(new Separator());
        Add(menu, "Главная", "", () => ShowMain("home"));
        Add(menu, "Добавить ограничение", "", () => OpenRuleEditor());
        Add(menu, "Статистика", "", () => ShowMain("stats"));
        Add(menu, "Настройки", "", () => ShowMain("settings"));
        menu.Items.Add(new Separator());
        if (Engine.IsPaused(now) || !Settings.Active)
            Add(menu, Settings.Active ? $"Возобновить (пауза до {Settings.PausedUntil:HH:mm})" : "Включить защиту", "", Resume);
        else
            Add(menu, "Приостановить на 1 час", "", () => { ShowMain(); Pause(_main, TimeSpan.FromHours(1)); });
        Add(menu, "Выход", "", RequestExit);
    }

    private static void Add(ContextMenu menu, string text, string glyph, Action action)
    {
        var item = new MenuItem { Header = text, Tag = glyph };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    // ================= Завершение =================

    public void RequestExit()
    {
        ShowMain();
        if (!ConfirmWeakening(_main, "Выйти из SelfLock?", "После выхода ограничения перестанут работать до следующего запуска. Блокировки сохранятся и продолжатся после запуска.", "Выйти"))
            return;
        _exiting = true;
        Application.Current.Shutdown();
    }

    public bool IsExiting => _exiting;

    public void SaveRuntime()
    {
        try { Store.SaveRuntime(); }
        catch (Exception ex) { App.Log(ex); }
    }

    public void SaveAll()
    {
        try { Store.SaveAll(); }
        catch (Exception ex) { App.Log(ex); }
    }

    public void Shutdown()
    {
        _exiting = true;
        _timer.Stop();
        _sampler.Dispose();
        SaveAll();
        _tray?.Dispose();
        _tray = null;
    }
}

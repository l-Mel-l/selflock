namespace SelfLock.Core;

public abstract record EngineEvent(Rule Rule);

/// <summary>До лимита осталось немного — мягкое напоминание.</summary>
public sealed record PreWarnEvent(Rule Rule, TimeSpan Left) : EngineEvent(Rule);

/// <summary>Лимит достигнут (мягкий режим): показать предупреждение с отсрочкой.</summary>
public sealed record WarningEvent(Rule Rule, DateTime Deadline, bool CanSnooze) : EngineEvent(Rule);

/// <summary>
/// Ресурс нужно закрыть. Fresh — правило только что сработало; иначе — пользователь снова открыл заблокированный ресурс.
/// Active — ресурс сейчас на переднем плане.
/// </summary>
public sealed record EnforceEvent(Rule Rule, bool Fresh, bool Active) : EngineEvent(Rule);

/// <summary>Блокировка закончилась.</summary>
public sealed record UnblockedEvent(Rule Rule) : EngineEvent(Rule);

/// <summary>
/// Сердце SelfLock: по ресурсу на переднем плане и прошедшему времени ведёт счётчики правил
/// и решает, когда предупредить, отложить и закрыть. Не знает ничего про WinAPI, поэтому покрыт тестами.
/// </summary>
public sealed class RuleEngine
{
    /// <summary>Тик длиннее этого считается сном/гибернацией/переводом часов и не засчитывается.</summary>
    public const double MaxTickSeconds = 5;

    private readonly Store _store;

    public RuleEngine(Store store) => _store = store;

    private AppSettings Settings => _store.Settings;

    public bool IsPaused(DateTime now) => Settings.PausedUntil is { } p && p > now;

    public bool IsRunning(DateTime now) => Settings.Active && Settings.PermissionsAccepted && !IsPaused(now);

    public RuleState StateOf(Rule rule)
    {
        if (!_store.States.TryGetValue(rule.Id, out var st)) _store.States[rule.Id] = st = new RuleState();
        return st;
    }

    public void ResetState(Rule rule) => _store.States[rule.Id] = new RuleState();

    public void Forget(Rule rule) => _store.States.Remove(rule.Id);

    public List<EngineEvent> Tick(DateTime now, double deltaSeconds, ForegroundInfo fg)
    {
        var delta = deltaSeconds is < 0 or > MaxTickSeconds ? 0 : deltaSeconds;
        var events = new List<EngineEvent>();

        if (Settings.PausedUntil is { } p && p <= now) Settings.PausedUntil = null;

        if (delta > 0 && fg.ProcessName.Length > 0)
            _store.Usage.Add(now, Matcher.UsageKey(fg), delta);

        if (!IsRunning(now)) return events;

        foreach (var rule in _store.Rules.Where(r => r.Enabled))
            TickRule(rule, now, delta, Matcher.Matches(rule, fg), events);

        return events;
    }

    private void TickRule(Rule rule, DateTime now, double delta, bool active, List<EngineEvent> events)
    {
        var st = StateOf(rule);

        // Новый день: напоминание и отсрочка снова доступны.
        if (st.Phase == RulePhase.Idle && st.LastSeen is { } seen && seen.Date != now.Date)
        {
            st.PreWarned = false;
            st.SnoozeUsed = false;
        }

        if (st.Phase == RulePhase.Blocked && st.Until <= now)
        {
            Reset(st);
            events.Add(new UnblockedEvent(rule));
        }

        if (st.Phase is RulePhase.Warning or RulePhase.Snoozed && st.Until <= now)
        {
            Block(rule, st, now);
            Record(rule, now, "closed");
            events.Add(new EnforceEvent(rule, Fresh: true, Active: active));
            return;
        }

        if (active)
        {
            if (delta > 0)
            {
                _store.Usage.AddRule(now, rule.Id, delta);
                if (st.LastSeen is { } last && (now - last).TotalMinutes > Settings.SessionResetMinutes)
                    st.SessionSeconds = 0;
                st.SessionSeconds += delta;
            }
            st.LastSeen = now;
        }

        if (st.Phase == RulePhase.Blocked)
        {
            if (active) events.Add(new EnforceEvent(rule, Fresh: false, Active: true));
            return;
        }

        if (st.Phase != RulePhase.Idle || !active) return;

        if (ConditionMet(rule, st, now))
        {
            if (rule.Mode == ReactionMode.Hard)
            {
                Block(rule, st, now);
                Record(rule, now, "hard");
                events.Add(new EnforceEvent(rule, Fresh: true, Active: true));
            }
            else
            {
                st.Phase = RulePhase.Warning;
                st.Until = now.AddSeconds(Math.Max(5, Settings.WarningSeconds));
                Record(rule, now, "warning");
                events.Add(new WarningEvent(rule, st.Until.Value, CanSnooze: !st.SnoozeUsed));
            }
            return;
        }

        if (!st.PreWarned && Settings.PreWarnMinutes > 0)
        {
            var left = Remaining(rule, now);
            if (left > TimeSpan.Zero && left <= TimeSpan.FromMinutes(Settings.PreWarnMinutes))
            {
                st.PreWarned = true;
                events.Add(new PreWarnEvent(rule, left));
            }
        }
    }

    /// <summary>Пользователь нажал «Отложить». Отсрочка даётся один раз за цикл и не длиннее заданной в правиле.</summary>
    public bool Snooze(Rule rule, DateTime now, int? minutes = null)
    {
        var st = StateOf(rule);
        if (st.Phase != RulePhase.Warning || st.SnoozeUsed) return false;
        var m = rule.SnoozeChoosable && minutes is { } chosen
            ? Math.Clamp(chosen, 1, Math.Max(1, rule.SnoozeMinutes))
            : Math.Max(1, rule.SnoozeMinutes);
        st.Phase = RulePhase.Snoozed;
        st.Until = now.AddMinutes(m);
        st.SnoozeUsed = true;
        Record(rule, now, "snooze");
        return true;
    }

    /// <summary>Пользователь сам нажал «Закрыть сейчас» в предупреждении (или досрочно во время отсрочки).</summary>
    public EnforceEvent? CloseNow(Rule rule, DateTime now)
    {
        var st = StateOf(rule);
        if (st.Phase is not (RulePhase.Warning or RulePhase.Snoozed)) return null;
        Block(rule, st, now);
        Record(rule, now, "closed");
        return new EnforceEvent(rule, Fresh: true, Active: true);
    }

    /// <summary>Сколько секунд засчитано правилу для отображения прогресса.</summary>
    public double UsedSeconds(Rule rule, DateTime now) => rule.Condition switch
    {
        ConditionKind.Continuous => EffectiveSession(StateOf(rule), now),
        ConditionKind.DailyTotal => _store.Usage.RuleSeconds(now, rule.Id),
        _ => _store.Usage.RuleSeconds(now, rule.Id),
    };

    /// <summary>Сколько осталось до срабатывания (для окна по времени — до начала окна).</summary>
    public TimeSpan Remaining(Rule rule, DateTime now)
    {
        switch (rule.Condition)
        {
            case ConditionKind.TimeWindow:
                if (InWindow(rule, now)) return TimeSpan.Zero;
                var start = now.Date.AddMinutes(rule.WindowStartMinutes);
                if (start <= now) start = start.AddDays(1);
                return start - now;
            default:
                var left = rule.LimitMinutes * 60 - UsedSeconds(rule, now);
                return TimeSpan.FromSeconds(Math.Max(0, left));
        }
    }

    public static bool InWindow(Rule rule, DateTime now)
    {
        int s = rule.WindowStartMinutes, e = rule.WindowEndMinutes, t = now.Hour * 60 + now.Minute;
        if (s == e) return true; // окно на сутки
        return s < e ? t >= s && t < e : t >= s || t < e;
    }

    public static DateTime WindowEnd(Rule rule, DateTime now)
    {
        var end = now.Date.AddMinutes(rule.WindowEndMinutes);
        if (end <= now) end = end.AddDays(1);
        return end;
    }

    public static DateTime BlockEnd(Rule rule, DateTime now) => rule.Condition switch
    {
        ConditionKind.Continuous => now.AddMinutes(Math.Max(1, rule.BlockMinutes)),
        ConditionKind.DailyTotal => now.Date.AddDays(1),
        ConditionKind.TimeWindow => WindowEnd(rule, now),
        _ => now.AddMinutes(30),
    };

    private bool ConditionMet(Rule rule, RuleState st, DateTime now) => rule.Condition switch
    {
        ConditionKind.Continuous => st.SessionSeconds >= rule.LimitMinutes * 60,
        ConditionKind.DailyTotal => _store.Usage.RuleSeconds(now, rule.Id) >= rule.LimitMinutes * 60,
        ConditionKind.TimeWindow => InWindow(rule, now),
        _ => false,
    };

    private double EffectiveSession(RuleState st, DateTime now) =>
        st.LastSeen is { } last && (now - last).TotalMinutes > Settings.SessionResetMinutes ? 0 : st.SessionSeconds;

    private static void Block(Rule rule, RuleState st, DateTime now)
    {
        st.Phase = RulePhase.Blocked;
        st.Until = BlockEnd(rule, now);
        st.PreWarned = false;
    }

    private static void Reset(RuleState st)
    {
        st.Phase = RulePhase.Idle;
        st.Until = null;
        st.SnoozeUsed = false;
        st.SessionSeconds = 0;
        st.PreWarned = false;
    }

    private void Record(Rule rule, DateTime now, string action) =>
        _store.Usage.Triggers.Add(new TriggerRecord { At = now, RuleId = rule.Id, RuleName = rule.Name, Action = action });
}

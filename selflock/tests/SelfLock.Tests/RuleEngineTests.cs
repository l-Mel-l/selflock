using SelfLock.Core;

namespace SelfLock.Tests;

public class RuleEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "selflock-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Store _store;
    private readonly RuleEngine _engine;
    private DateTime _now = new(2026, 10, 13, 12, 0, 0);

    private static readonly ForegroundInfo YouTube = new("chrome", "YouTube", "https://www.youtube.com/watch?v=1", true);
    private static readonly ForegroundInfo Shorts = new("chrome", "YouTube", "https://www.youtube.com/shorts/1", true);
    private static readonly ForegroundInfo Work = new("code", "Program.cs - Visual Studio Code", null, false);

    public RuleEngineTests()
    {
        _store = new Store(_dir);
        _store.Settings.PermissionsAccepted = true;
        _store.Settings.WarningSeconds = 30;
        _store.Settings.PreWarnMinutes = 0;
        _engine = new RuleEngine(_store);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private Rule Add(Rule r)
    {
        _store.Rules.Add(r);
        return r;
    }

    /// <summary>Прогоняет seconds тиков по одной секунде и собирает все события.</summary>
    private List<EngineEvent> Run(int seconds, ForegroundInfo fg)
    {
        var all = new List<EngineEvent>();
        for (var i = 0; i < seconds; i++)
        {
            _now = _now.AddSeconds(1);
            all.AddRange(_engine.Tick(_now, 1, fg));
        }
        return all;
    }

    [Fact]
    public void Soft_rule_warns_after_continuous_limit()
    {
        var rule = Add(new Rule { Name = "YouTube", Kind = TargetKind.Site, Target = "youtube.com", LimitMinutes = 1 });

        Assert.DoesNotContain(Run(59, YouTube), e => e is WarningEvent);
        var events = Run(1, YouTube);

        var w = Assert.Single(events.OfType<WarningEvent>());
        Assert.True(w.CanSnooze);
        Assert.Equal(RulePhase.Warning, _engine.StateOf(rule).Phase);
    }

    [Fact]
    public void Ignored_warning_closes_resource_after_countdown()
    {
        var rule = Add(new Rule { Kind = TargetKind.Site, Target = "youtube.com", LimitMinutes = 1 });
        Run(60, YouTube);

        var events = Run(30, YouTube);

        var e = Assert.Single(events.OfType<EnforceEvent>(), x => x.Fresh);
        Assert.Equal(rule, e.Rule);
        Assert.Equal(RulePhase.Blocked, _engine.StateOf(rule).Phase);
    }

    [Fact]
    public void Snooze_is_allowed_only_once_then_resource_closes()
    {
        var rule = Add(new Rule { Kind = TargetKind.Site, Target = "youtube.com", LimitMinutes = 1, SnoozeMinutes = 2 });
        Run(60, YouTube);

        Assert.True(_engine.Snooze(rule, _now));
        Assert.False(_engine.Snooze(rule, _now)); // повторно — нельзя

        Assert.DoesNotContain(Run(119, YouTube), e => e is EnforceEvent);
        Assert.Contains(Run(1, YouTube), e => e is EnforceEvent { Fresh: true });
        Assert.Equal(RulePhase.Blocked, _engine.StateOf(rule).Phase);
    }

    [Fact]
    public void Snooze_length_cannot_exceed_rule_value()
    {
        var rule = Add(new Rule { Kind = TargetKind.Site, Target = "youtube.com", LimitMinutes = 1, SnoozeMinutes = 5, SnoozeChoosable = true });
        Run(60, YouTube);

        _engine.Snooze(rule, _now, minutes: 60);

        Assert.Equal(_now.AddMinutes(5), _engine.StateOf(rule).Until);
    }

    [Fact]
    public void Hard_rule_closes_immediately_without_warning()
    {
        var rule = Add(new Rule { Kind = TargetKind.Site, Target = "youtube.com/shorts", LimitMinutes = 1, Mode = ReactionMode.Hard });

        var events = Run(60, Shorts);

        Assert.DoesNotContain(events, e => e is WarningEvent);
        Assert.Contains(events, e => e is EnforceEvent { Fresh: true });
        Assert.False(_engine.Snooze(rule, _now));
    }

    [Fact]
    public void Blocked_resource_is_closed_again_when_reopened()
    {
        var rule = Add(new Rule { Kind = TargetKind.Site, Target = "youtube.com", LimitMinutes = 1, Mode = ReactionMode.Hard, BlockMinutes = 10 });
        Run(60, YouTube);

        Assert.DoesNotContain(Run(5, Work), e => e is EnforceEvent);
        Assert.Contains(Run(1, YouTube), e => e is EnforceEvent { Fresh: false });
    }

    [Fact]
    public void Block_ends_after_block_minutes()
    {
        var rule = Add(new Rule { Kind = TargetKind.Site, Target = "youtube.com", LimitMinutes = 1, Mode = ReactionMode.Hard, BlockMinutes = 1 });
        Run(60, YouTube);

        Assert.Contains(Run(61, Work), e => e is UnblockedEvent);
        Assert.Equal(RulePhase.Idle, _engine.StateOf(rule).Phase);
        Assert.Equal(0, _engine.StateOf(rule).SessionSeconds);
    }

    [Fact]
    public void Short_break_keeps_session_long_break_resets_it()
    {
        _store.Settings.SessionResetMinutes = 5;
        var rule = Add(new Rule { Kind = TargetKind.Site, Target = "youtube.com", LimitMinutes = 10 });

        Run(60, YouTube);
        Run(60, Work);                       // перерыв 1 мин — сеанс продолжается
        Run(1, YouTube);
        Assert.Equal(61, _engine.StateOf(rule).SessionSeconds);

        Run(6 * 60, Work);                   // перерыв 6 мин — сеанс заново
        Run(1, YouTube);
        Assert.Equal(1, _engine.StateOf(rule).SessionSeconds);
    }

    [Fact]
    public void Daily_limit_sums_separate_visits_and_blocks_until_midnight()
    {
        var rule = Add(new Rule { Kind = TargetKind.App, Target = "steam", Condition = ConditionKind.DailyTotal, LimitMinutes = 2, Mode = ReactionMode.Hard });
        var steam = new ForegroundInfo("steam", "Steam", null, false);

        Run(60, steam);
        Run(30 * 60, Work);
        var events = Run(60, steam);

        Assert.Contains(events, e => e is EnforceEvent { Fresh: true });
        Assert.Equal(_now.Date.AddDays(1), _engine.StateOf(rule).Until);
    }

    [Fact]
    public void Time_window_spanning_midnight()
    {
        var rule = new Rule { Condition = ConditionKind.TimeWindow, WindowStartMinutes = 22 * 60, WindowEndMinutes = 7 * 60 };

        Assert.True(RuleEngine.InWindow(rule, new DateTime(2026, 10, 13, 23, 30, 0)));
        Assert.True(RuleEngine.InWindow(rule, new DateTime(2026, 10, 14, 6, 59, 0)));
        Assert.False(RuleEngine.InWindow(rule, new DateTime(2026, 10, 14, 7, 0, 0)));
        Assert.Equal(new DateTime(2026, 10, 14, 7, 0, 0), RuleEngine.WindowEnd(rule, new DateTime(2026, 10, 13, 23, 30, 0)));
    }

    [Fact]
    public void Time_window_rule_fires_as_soon_as_resource_opened_inside_window()
    {
        _now = new DateTime(2026, 10, 13, 22, 59, 0);
        var rule = Add(new Rule { Kind = TargetKind.Site, Target = "youtube.com", Condition = ConditionKind.TimeWindow, WindowStartMinutes = 23 * 60, WindowEndMinutes = 7 * 60, Mode = ReactionMode.Hard });

        Assert.DoesNotContain(Run(59, YouTube), e => e is EnforceEvent);
        Assert.Contains(Run(2, YouTube), e => e is EnforceEvent { Fresh: true });
        Assert.Equal(new DateTime(2026, 10, 14, 7, 0, 0), _engine.StateOf(rule).Until);
    }

    [Fact]
    public void Sleep_gap_is_not_counted()
    {
        var rule = Add(new Rule { Kind = TargetKind.Site, Target = "youtube.com", LimitMinutes = 30 });
        Run(10, YouTube);

        _now = _now.AddHours(2);                      // ноутбук спал
        _engine.Tick(_now, 7200, YouTube);

        Assert.True(_engine.StateOf(rule).SessionSeconds <= 10);
        Assert.True(_store.Usage.RuleSeconds(_now, rule.Id) <= 10);
    }

    [Fact]
    public void Nothing_fires_while_paused_or_without_permissions()
    {
        Add(new Rule { Kind = TargetKind.Site, Target = "youtube.com", LimitMinutes = 1, Mode = ReactionMode.Hard });

        _store.Settings.PausedUntil = _now.AddHours(1);
        Assert.Empty(Run(120, YouTube));

        _store.Settings.PausedUntil = null;
        _store.Settings.PermissionsAccepted = false;
        Assert.Empty(Run(120, YouTube));
    }

    [Fact]
    public void Pre_warning_is_shown_once()
    {
        _store.Settings.PreWarnMinutes = 1;
        Add(new Rule { Kind = TargetKind.Site, Target = "youtube.com", LimitMinutes = 3 });

        var events = Run(150, YouTube);

        Assert.Single(events.OfType<PreWarnEvent>());
    }

    [Fact]
    public void Disabled_rule_is_ignored()
    {
        Add(new Rule { Kind = TargetKind.Site, Target = "youtube.com", LimitMinutes = 1, Mode = ReactionMode.Hard, Enabled = false });
        Assert.Empty(Run(120, YouTube));
    }

    [Fact]
    public void Store_roundtrip_keeps_rules_state_and_usage()
    {
        var rule = Add(new Rule { Name = "VK", Kind = TargetKind.Site, Target = "vk.com", Mode = ReactionMode.Hard, LimitMinutes = 1 });
        Run(60, new ForegroundInfo("chrome", "VK", "vk.com/feed", true));
        _store.SaveAll();

        var loaded = new Store(_dir);
        loaded.Load();

        Assert.Equal("VK", Assert.Single(loaded.Rules).Name);
        Assert.Equal(ReactionMode.Hard, loaded.Rules[0].Mode);
        Assert.Equal(RulePhase.Blocked, loaded.States[rule.Id].Phase);
        Assert.Equal(60, loaded.Usage.RuleSeconds(_now, rule.Id));
    }
}

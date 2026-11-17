using SelfLock.Core;

namespace SelfLock.Tests;

public class RuleDuplicatesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "selflock-dup-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Store _store;

    public RuleDuplicatesTests() => _store = new Store(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Rule Shorts() => new()
    {
        Name = "YouTube Shorts",
        Kind = TargetKind.Site,
        Target = "youtube.com/shorts",
        LimitMinutes = 15,
        Mode = ReactionMode.Hard,
        BlockMinutes = 60,
    };

    [Fact]
    public void Second_quick_rule_is_rejected_and_list_keeps_one_entry()
    {
        var first = Shorts();
        var second = Shorts();

        Assert.True(_store.TryAddRule(first, out var none));
        Assert.Null(none);
        Assert.False(_store.TryAddRule(second, out var existing));
        Assert.Same(first, existing);
        Assert.Single(_store.Rules);
    }

    [Fact]
    public void Same_site_after_url_normalization_is_a_duplicate()
    {
        var first = Shorts();
        var second = Shorts();
        second.Target = "https://www.YouTube.com/shorts/";

        Assert.True(_store.TryAddRule(first, out _));
        Assert.False(_store.TryAddRule(second, out var existing));
        Assert.Same(first, existing);
    }

    [Fact]
    public void Different_time_windows_for_same_site_are_not_duplicates()
    {
        var night = Window(22 * 60, 7 * 60);
        var day = Window(9 * 60, 18 * 60);

        Assert.Null(RuleDuplicates.Find([night], day));
        Assert.True(_store.TryAddRule(night, out _));
        Assert.True(_store.TryAddRule(day, out _));
        Assert.Equal(2, _store.Rules.Count);
    }

    [Fact]
    public void Same_time_window_for_same_site_is_a_duplicate()
    {
        var first = Window(22 * 60, 7 * 60);
        var second = Window(22 * 60, 7 * 60);

        Assert.Same(first, RuleDuplicates.Find([first], second));
        Assert.True(_store.TryAddRule(first, out _));
        Assert.False(_store.TryAddRule(second, out var existing));
        Assert.Same(first, existing);
    }

    private static Rule Window(int start, int end) => new()
    {
        Name = $"YouTube {start}-{end}",
        Kind = TargetKind.Site,
        Target = "youtube.com",
        Condition = ConditionKind.TimeWindow,
        WindowStartMinutes = start,
        WindowEndMinutes = end,
    };
}

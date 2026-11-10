using SelfLock.Core;

namespace SelfLock.Tests;

public class RuleDuplicatesTests
{
    [Fact]
    public void Different_time_windows_for_same_site_are_not_duplicates()
    {
        var night = Window(22 * 60, 7 * 60);
        var day = Window(9 * 60, 18 * 60);

        Assert.Null(RuleDuplicates.Find([night], day));
    }

    [Fact]
    public void Same_time_window_for_same_site_is_a_duplicate()
    {
        var first = Window(22 * 60, 7 * 60);
        var second = Window(22 * 60, 7 * 60);

        Assert.Same(first, RuleDuplicates.Find([first], second));
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

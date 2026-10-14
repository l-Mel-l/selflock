using SelfLock.Core;

namespace SelfLock.UI;

public static class Format
{
    public static string Duration(double seconds)
    {
        var s = (int)Math.Round(Math.Max(0, seconds));
        if (s < 60) return $"{s} сек";
        var m = s / 60;
        if (m < 60) return $"{m} мин";
        var h = m / 60;
        m %= 60;
        return m == 0 ? $"{h} ч" : $"{h} ч {m} мин";
    }

    public static string Short(double seconds)
    {
        var m = (int)(Math.Max(0, seconds) / 60);
        return m < 60 ? $"{m}м" : $"{m / 60}ч {m % 60:00}м";
    }

    public static string Countdown(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    public static string Hm(int minutes) => $"{minutes / 60 % 24:00}:{minutes % 60:00}";

    public static string Until(DateTime when, DateTime now) =>
        when.Date == now.Date ? $"до {when:HH:mm}"
        : when.Date == now.Date.AddDays(1) ? (when.TimeOfDay == TimeSpan.Zero ? "до конца дня" : $"до завтра {when:HH:mm}")
        : $"до {when:dd.MM HH:mm}";

    public static string At(DateTime when, DateTime now) =>
        when.Date == now.Date ? $"в {when:HH:mm}"
        : when.Date == now.Date.AddDays(1) ? (when.TimeOfDay == TimeSpan.Zero ? "в полночь" : $"завтра в {when:HH:mm}")
        : $"{when:dd.MM} в {when:HH:mm}";

    public static string Condition(Rule r) => r.Condition switch
    {
        ConditionKind.Continuous => $"{Duration(r.LimitMinutes * 60)} подряд",
        ConditionKind.DailyTotal => $"{Duration(r.LimitMinutes * 60)} в день",
        ConditionKind.TimeWindow => $"запрет {Hm(r.WindowStartMinutes)}–{Hm(r.WindowEndMinutes)}",
        _ => "",
    };

    public static string Mode(Rule r) => r.Mode == ReactionMode.Hard
        ? "жёсткий режим"
        : $"мягкий · отсрочка {(r.SnoozeChoosable ? "до " : "")}{r.SnoozeMinutes} мин";

    public static string Target(Rule r) => r.Kind == TargetKind.App
        ? $"приложение · {r.Target}.exe"
        : $"сайт · {r.Target}";

    public static string Plural(int n, string one, string few, string many)
    {
        var a = Math.Abs(n) % 100;
        var b = a % 10;
        if (a is > 10 and < 20) return many;
        return b == 1 ? one : b is >= 2 and <= 4 ? few : many;
    }
}

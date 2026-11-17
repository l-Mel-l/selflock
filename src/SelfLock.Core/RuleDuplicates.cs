namespace SelfLock.Core;

/// <summary>
/// Единое правило проверки дубликатов: два ограничения не должны одновременно
/// действовать на один ресурс с одинаковыми параметрами условия.
/// </summary>
public static class RuleDuplicates
{
    /// <summary>Возвращает уже существующее правило, совпадающее с кандидатом, или null.</summary>
    public static Rule? Find(IEnumerable<Rule> rules, Rule candidate) =>
        rules.FirstOrDefault(x => IsDuplicate(x, candidate));

    public static bool IsDuplicate(Rule existing, Rule candidate) =>
        existing.Id != candidate.Id
        && existing.Kind == candidate.Kind
        && TargetKey(existing) == TargetKey(candidate)
        && SameCondition(existing, candidate);

    /// <summary>
    /// Для интервала «время суток» сравниваются его границы: два разных окна
    /// для одного сайта — не дубликаты. Для остальных условий параметры те же.
    /// </summary>
    public static bool SameCondition(Rule a, Rule b) =>
        a.Condition == b.Condition && a.Condition switch
        {
            ConditionKind.TimeWindow => a.WindowStartMinutes == b.WindowStartMinutes
                                        && a.WindowEndMinutes == b.WindowEndMinutes,
            _ => true,
        };

    private static string TargetKey(Rule rule) => rule.Kind == TargetKind.App
        ? Matcher.NormalizeProcess(rule.Target)
        : Matcher.NormalizeUrl(rule.Target);
}

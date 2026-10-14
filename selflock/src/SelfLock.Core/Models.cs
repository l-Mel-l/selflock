namespace SelfLock.Core;

/// <summary>Что ограничиваем: приложение (процесс) или сайт (адрес в браузере).</summary>
public enum TargetKind { App, Site }

/// <summary>Условие срабатывания правила.</summary>
public enum ConditionKind
{
    /// <summary>N минут непрерывного пребывания (короткие перерывы не сбрасывают сеанс).</summary>
    Continuous,
    /// <summary>Суммарно N минут за день.</summary>
    DailyTotal,
    /// <summary>Запрещённое время суток (например, 22:00–07:00).</summary>
    TimeWindow,
}

/// <summary>Режим реакции на срабатывание.</summary>
public enum ReactionMode
{
    /// <summary>Предупреждение + однократная отсрочка, затем закрытие.</summary>
    Soft,
    /// <summary>Закрытие сразу, без отсрочки.</summary>
    Hard,
}

public sealed class Rule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public TargetKind Kind { get; set; }

    /// <summary>Имя процесса без .exe (для приложений) или шаблон адреса, например "youtube.com/shorts".</summary>
    public string Target { get; set; } = "";

    /// <summary>Путь к exe — только для иконки и красивого имени.</summary>
    public string? ExePath { get; set; }

    public ConditionKind Condition { get; set; } = ConditionKind.Continuous;
    public int LimitMinutes { get; set; } = 30;

    /// <summary>Начало запрещённого окна, минуты от полуночи.</summary>
    public int WindowStartMinutes { get; set; } = 22 * 60;
    /// <summary>Конец запрещённого окна, минуты от полуночи (может быть меньше начала — окно через полночь).</summary>
    public int WindowEndMinutes { get; set; } = 7 * 60;

    public ReactionMode Mode { get; set; } = ReactionMode.Soft;

    /// <summary>Длительность отсрочки (мягкий режим), заранее заданная пользователем.</summary>
    public int SnoozeMinutes { get; set; } = 5;

    /// <summary>Разрешить в момент предупреждения выбрать отсрочку короче максимальной.</summary>
    public bool SnoozeChoosable { get; set; }

    /// <summary>На сколько минут блокировать повторное открытие после срабатывания (для «непрерывного» условия).</summary>
    public int BlockMinutes { get; set; } = 30;

    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public Rule Clone() => (Rule)MemberwiseClone();
}

public sealed class AppSettings
{
    /// <summary>Глобальный выключатель защиты.</summary>
    public bool Active { get; set; } = true;
    public DateTime? PausedUntil { get; set; }

    public bool AutoStart { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;

    /// <summary>Сколько секунд ждать перед подтверждением ослабления защиты (0 — без задержки).</summary>
    public int ProtectionDelaySeconds { get; set; } = 30;
    /// <summary>Нельзя менять/удалять правило, пока оно блокирует ресурс.</summary>
    public bool LockRulesWhileBlocked { get; set; } = true;

    /// <summary>Сколько секунд висит предупреждение, прежде чем ресурс закроется сам.</summary>
    public int WarningSeconds { get; set; } = 30;
    /// <summary>Перерыв (мин), после которого «непрерывный» сеанс начинается заново.</summary>
    public int SessionResetMinutes { get; set; } = 5;
    /// <summary>За сколько минут до лимита показать мягкое напоминание (0 — не показывать).</summary>
    public int PreWarnMinutes { get; set; } = 5;

    /// <summary>Пользователь ознакомился с тем, какой доступ нужен приложению, и разрешил его.</summary>
    public bool PermissionsAccepted { get; set; }
}

public enum RulePhase { Idle, Warning, Snoozed, Blocked }

/// <summary>Состояние правила во время работы. Сохраняется на диск, чтобы блокировка пережила перезагрузку.</summary>
public sealed class RuleState
{
    public RulePhase Phase { get; set; } = RulePhase.Idle;
    /// <summary>Когда закончится текущая фаза (предупреждение / отсрочка / блокировка).</summary>
    public DateTime? Until { get; set; }
    public bool SnoozeUsed { get; set; }
    /// <summary>Длина текущего непрерывного сеанса, сек.</summary>
    public double SessionSeconds { get; set; }
    /// <summary>Когда ресурс последний раз был активен.</summary>
    public DateTime? LastSeen { get; set; }
    public bool PreWarned { get; set; }
}

/// <summary>Что сейчас на переднем плане.</summary>
public sealed record ForegroundInfo(string ProcessName, string Title, string? Url, bool IsBrowser)
{
    public static readonly ForegroundInfo None = new("", "", null, false);
}

public sealed class AppInfo
{
    public string Name { get; set; } = "";
    public string? Path { get; set; }
}

public sealed class TriggerRecord
{
    public DateTime At { get; set; }
    public string RuleId { get; set; } = "";
    public string RuleName { get; set; } = "";
    /// <summary>warning | snooze | closed | hard</summary>
    public string Action { get; set; } = "";
}

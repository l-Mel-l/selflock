using System.Text.Json;
using System.Text.Json.Serialization;

namespace SelfLock.Core;

public sealed class UsageData
{
    /// <summary>"yyyy-MM-dd" → (ключ ресурса → секунды на переднем плане).</summary>
    public Dictionary<string, Dictionary<string, double>> Days { get; set; } = new();
    /// <summary>"yyyy-MM-dd" → (id правила → секунды, засчитанные правилу).</summary>
    public Dictionary<string, Dictionary<string, double>> RuleDays { get; set; } = new();
    public List<TriggerRecord> Triggers { get; set; } = new();
    /// <summary>Имя процесса → человекочитаемое имя и путь к exe.</summary>
    public Dictionary<string, AppInfo> Apps { get; set; } = new();

    public static string DayKey(DateTime d) => d.ToString("yyyy-MM-dd");

    public void Add(DateTime day, string key, double seconds) =>
        Bump(Days, DayKey(day), key, seconds);

    public void AddRule(DateTime day, string ruleId, double seconds) =>
        Bump(RuleDays, DayKey(day), ruleId, seconds);

    public double RuleSeconds(DateTime day, string ruleId) =>
        RuleDays.TryGetValue(DayKey(day), out var d) && d.TryGetValue(ruleId, out var s) ? s : 0;

    public IReadOnlyDictionary<string, double> Day(DateTime day) =>
        Days.TryGetValue(DayKey(day), out var d) ? d : new Dictionary<string, double>();

    public double DayTotal(DateTime day) => Day(day).Values.Sum();

    public double RuleDayTotal(DateTime day) =>
        RuleDays.TryGetValue(DayKey(day), out var d) ? d.Values.Sum() : 0;

    /// <summary>Удаляет статистику старше keepDays дней.</summary>
    public void Prune(DateTime today, int keepDays = 120)
    {
        var limit = DayKey(today.AddDays(-keepDays));
        foreach (var k in Days.Keys.Where(k => string.CompareOrdinal(k, limit) < 0).ToList()) Days.Remove(k);
        foreach (var k in RuleDays.Keys.Where(k => string.CompareOrdinal(k, limit) < 0).ToList()) RuleDays.Remove(k);
        Triggers.RemoveAll(t => t.At < today.AddDays(-keepDays));
    }

    private static void Bump(Dictionary<string, Dictionary<string, double>> map, string day, string key, double s)
    {
        if (!map.TryGetValue(day, out var d)) map[day] = d = new();
        d[key] = d.GetValueOrDefault(key) + s;
    }
}

/// <summary>Всё, что приложение хранит локально (без интернета): правила, настройки, состояние, статистика.</summary>
public sealed class Store
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Directory { get; }
    public List<Rule> Rules { get; private set; } = new();
    public AppSettings Settings { get; private set; } = new();
    public Dictionary<string, RuleState> States { get; private set; } = new();
    public UsageData Usage { get; private set; } = new();

    public Store(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SelfLock");

    public void Load()
    {
        Rules = Read<List<Rule>>("rules.json") ?? new();
        Settings = Read<AppSettings>("settings.json") ?? new();
        States = Read<Dictionary<string, RuleState>>("state.json") ?? new();
        Usage = Read<UsageData>("usage.json") ?? new();
    }

    public void SaveRules() => Write("rules.json", Rules);
    public void SaveSettings() => Write("settings.json", Settings);
    public void SaveRuntime()
    {
        Write("state.json", States);
        Write("usage.json", Usage);
    }

    public void SaveAll()
    {
        SaveRules();
        SaveSettings();
        SaveRuntime();
    }

    private T? Read<T>(string file) where T : class
    {
        var path = Path.Combine(Directory, file);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException)
        {
            // Повреждённый файл не должен ронять приложение: откладываем его в сторону и начинаем с чистого.
            File.Copy(path, path + ".broken", overwrite: true);
            return null;
        }
    }

    private void Write<T>(string file, T value)
    {
        var path = Path.Combine(Directory, file);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }
}

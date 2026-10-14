namespace SelfLock.Core;

/// <summary>Сопоставляет то, что сейчас на экране, с правилами.</summary>
public static class Matcher
{
    public static readonly string[] Browsers =
        ["chrome", "msedge", "firefox", "opera", "brave", "browser", "vivaldi", "yandex", "chromium", "arc"];

    /// <summary>Процессы, которые нельзя ограничивать: иначе можно «уронить» Windows или сам SelfLock.</summary>
    public static readonly string[] Protected =
        ["explorer", "svchost", "csrss", "wininit", "winlogon", "dwm", "lsass", "services", "smss",
         "system", "idle", "taskmgr", "selflock", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
         "lockapp", "sihost", "fontdrvhost", "conhost", "ctfmon", "runtimebroker"];

    public static bool IsBrowser(string processName) =>
        Browsers.Contains(NormalizeProcess(processName));

    public static bool IsProtected(string processName) =>
        Protected.Contains(NormalizeProcess(processName));

    public static string NormalizeProcess(string name)
    {
        name = name.Trim().ToLowerInvariant();
        return name.EndsWith(".exe") ? name[..^4] : name;
    }

    /// <summary>"https://www.YouTube.com/shorts/abc?x=1" → "youtube.com/shorts/abc?x=1"</summary>
    public static string NormalizeUrl(string url)
    {
        var u = url.Trim().ToLowerInvariant();
        var scheme = u.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) u = u[(scheme + 3)..];
        if (u.StartsWith("www.")) u = u[4..];
        else if (u.StartsWith("m.")) u = u[2..];
        return u.TrimEnd('/');
    }

    public static (string Host, string Path) SplitUrl(string normalized)
    {
        var cut = normalized.IndexOfAny(['/', '?', '#']);
        return cut < 0 ? (normalized, "") : (normalized[..cut], normalized[cut..]);
    }

    public static string HostOf(string url) => SplitUrl(NormalizeUrl(url)).Host;

    public static bool UrlMatches(string pattern, string url)
    {
        var (ph, pp) = SplitUrl(NormalizeUrl(pattern));
        var (uh, up) = SplitUrl(NormalizeUrl(url));
        if (ph.Length == 0) return false;
        // youtube.com покрывает music.youtube.com, но не notyoutube.com
        var hostOk = uh == ph || uh.EndsWith("." + ph, StringComparison.Ordinal);
        if (!hostOk) return false;
        return pp.Length == 0 || up.StartsWith(pp, StringComparison.Ordinal);
    }

    /// <summary>Слово, по которому сайт узнаётся в заголовке окна, если адрес прочитать не удалось.</summary>
    public static string TitleHint(string pattern)
    {
        var host = SplitUrl(NormalizeUrl(pattern)).Host;
        var dot = host.LastIndexOf('.');
        return dot > 0 ? host[..dot].Split('.').Last() : host;
    }

    public static bool Matches(Rule rule, ForegroundInfo fg)
    {
        if (string.IsNullOrWhiteSpace(rule.Target) || fg.ProcessName.Length == 0) return false;

        if (rule.Kind == TargetKind.App)
            return NormalizeProcess(fg.ProcessName) == NormalizeProcess(rule.Target);

        if (!fg.IsBrowser) return false;
        if (!string.IsNullOrEmpty(fg.Url)) return UrlMatches(rule.Target, fg.Url);

        // Адрес не прочитался — узнаём сайт по заголовку, но только если в правиле нет пути
        // (у YouTube Shorts заголовок ничем не отличается от обычного YouTube).
        var (_, path) = SplitUrl(NormalizeUrl(rule.Target));
        if (path.Length > 0) return false;
        var hint = TitleHint(rule.Target);
        return hint.Length >= 2 && fg.Title.Contains(hint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ключ для статистики: сайт для браузера (если адрес известен), иначе процесс.</summary>
    public static string UsageKey(ForegroundInfo fg)
    {
        if (fg.IsBrowser && !string.IsNullOrEmpty(fg.Url))
        {
            var host = HostOf(fg.Url);
            if (host.Contains('.')) return "site:" + host;
        }
        return "app:" + NormalizeProcess(fg.ProcessName);
    }
}

using System.Diagnostics;
using SelfLock.Core;
using SelfLock.Native;

namespace SelfLock.Services;

public sealed record ForegroundSample(IntPtr Hwnd, ForegroundInfo Info, string? Path, DateTime At, bool IsSelf)
{
    public static readonly ForegroundSample Empty = new(IntPtr.Zero, ForegroundInfo.None, null, DateTime.MinValue, false);
}

/// <summary>
/// Фоновый поток, который 2–3 раза в секунду смотрит, какое окно активно (WinAPI),
/// а для браузеров читает адресную строку (UI Automation). Сам ничего не решает — только публикует снимок.
/// </summary>
public sealed class ForegroundSampler : IDisposable
{
    private readonly Thread _thread;
    private readonly BrowserUrlReader _urls = new();
    private readonly Dictionary<uint, (string Name, string? Path)> _procs = new();
    private readonly uint _selfPid = (uint)Environment.ProcessId;
    private volatile ForegroundSample _latest = ForegroundSample.Empty;
    private volatile bool _stop;

    public ForegroundSampler()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "SelfLock sampler" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public ForegroundSample Latest => _latest;

    /// <summary>Удалось ли хоть раз прочитать адрес из браузера (для экрана разрешений).</summary>
    public bool UrlReadWorked => _urls.EverSucceeded;

    public void Start() => _thread.Start();

    public void Dispose() => _stop = true;

    private void Loop()
    {
        while (!_stop)
        {
            try { _latest = Sample(); }
            catch { _latest = ForegroundSample.Empty with { At = DateTime.Now }; }
            Thread.Sleep(350);
        }
    }

    private ForegroundSample Sample()
    {
        if (TestMode.FakeForeground is { } fake)
        {
            var parts = fake.Split('|', 2);
            var proc = Matcher.NormalizeProcess(parts[0]);
            return new ForegroundSample(IntPtr.Zero, new ForegroundInfo(proc, "", parts.Length > 1 ? parts[1] : null, Matcher.IsBrowser(proc)), null, DateTime.Now, false);
        }

        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return ForegroundSample.Empty with { At = DateTime.Now };

        var pid = Win32.GetPid(hwnd);
        if (pid == _selfPid)
            return new ForegroundSample(hwnd, new ForegroundInfo("selflock", "", null, false), null, DateTime.Now, true);

        var (name, path) = Proc(pid);

        // Приложения из Microsoft Store живут внутри ApplicationFrameHost — ищем настоящий процесс в дочерних окнах.
        if (name == "applicationframehost")
        {
            uint inner = 0;
            Win32.EnumChildWindows(hwnd, (child, _) =>
            {
                var p = Win32.GetPid(child);
                if (p != pid) { inner = p; return false; }
                return true;
            }, IntPtr.Zero);
            if (inner != 0) (name, path) = Proc(inner);
        }

        var title = Win32.GetTitle(hwnd);
        var browser = Matcher.IsBrowser(name);
        var url = browser ? _urls.Read(hwnd, name) : null;
        return new ForegroundSample(hwnd, new ForegroundInfo(name, title, url, browser), path, DateTime.Now, false);
    }

    private (string Name, string? Path) Proc(uint pid)
    {
        if (_procs.TryGetValue(pid, out var cached)) return cached;
        string name;
        try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName.ToLowerInvariant(); }
        catch { name = ""; }
        var entry = (name, Win32.GetProcessPath(pid));
        if (_procs.Count > 500) _procs.Clear();
        _procs[pid] = entry;
        return entry;
    }
}

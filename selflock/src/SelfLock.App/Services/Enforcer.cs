using System.Diagnostics;
using SelfLock.Core;
using SelfLock.Native;

namespace SelfLock.Services;

/// <summary>Исполняет решение движка: закрывает вкладку сайта или приложение.</summary>
public sealed class Enforcer
{
    private readonly Dictionary<string, DateTime> _lastAction = new();

    /// <summary>
    /// Закрывает вкладку (Ctrl+W) — только если снимок свежий и окно браузера всё ещё активно,
    /// чтобы случайно не закрыть другую вкладку.
    /// </summary>
    public bool CloseSite(Rule rule, ForegroundSample sample)
    {
        var last = _lastAction.GetValueOrDefault(rule.Id);
        if (DateTime.Now - last < TimeSpan.FromSeconds(1.2)) return false;
        if (sample.At <= last.AddMilliseconds(300)) return false; // снимок сделан до прошлого закрытия
        if (!sample.Info.IsBrowser || Win32.GetForegroundWindow() != sample.Hwnd) return false;

        Win32.SendChord(Win32.VK_CONTROL, Win32.VK_W);
        _lastAction[rule.Id] = DateTime.Now;
        return true;
    }

    /// <summary>Закрывает все процессы приложения: сначала вежливо, потом принудительно, в крайнем случае сворачивает.</summary>
    public bool CloseApp(Rule rule)
    {
        var name = Matcher.NormalizeProcess(rule.Target);
        if (Matcher.IsProtected(name)) return false;
        if (DateTime.Now - _lastAction.GetValueOrDefault(rule.Id) < TimeSpan.FromSeconds(2)) return false;

        var procs = Process.GetProcessesByName(name);
        if (procs.Length == 0) return false;
        _lastAction[rule.Id] = DateTime.Now;

        Task.Run(() =>
        {
            foreach (var p in procs)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow();
                }
                catch { /* процесс уже завершился */ }
            }
            Thread.Sleep(1500);
            foreach (var p in procs)
            {
                try
                {
                    if (!p.HasExited) p.Kill();
                }
                catch
                {
                    // Нет прав (например, приложение запущено от администратора) — хотя бы убираем окно с глаз.
                    try { if (p.MainWindowHandle != IntPtr.Zero) Win32.ShowWindow(p.MainWindowHandle, Win32.SW_MINIMIZE); } catch { }
                }
                finally { p.Dispose(); }
            }
        });
        return true;
    }

    public static bool IsAppRunning(Rule rule) =>
        Process.GetProcessesByName(Matcher.NormalizeProcess(rule.Target)) is { Length: > 0 } list && Dispose(list);

    private static bool Dispose(Process[] list)
    {
        foreach (var p in list) p.Dispose();
        return true;
    }
}

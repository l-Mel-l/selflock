using System.Diagnostics;
using System.IO;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SelfLock.Core;
using SelfLock.Native;

namespace SelfLock.Services;

/// <summary>Автозапуск вместе с Windows через HKCU\...\Run (права администратора не нужны).</summary>
public static class AutoStart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "SelfLock";

    private static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValue(Name) is string s && s.Equals(Command, StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) k.SetValue(Name, Command);
        else if (k.GetValue(Name) is not null) k.DeleteValue(Name);
    }
}

/// <summary>Иконки exe-файлов для списков.</summary>
public static class IconCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? ForFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        if (Cache.TryGetValue(path, out var img)) return img;

        var info = new Win32.SHFILEINFO();
        Win32.SHGetFileInfo(path, 0, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf(info), Win32.SHGFI_ICON | Win32.SHGFI_LARGEICON);
        if (info.hIcon != IntPtr.Zero)
        {
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                img = src;
            }
            finally { Win32.DestroyIcon(info.hIcon); }
        }
        return Cache[path] = img;
    }
}

public sealed record RunningApp(string Process, string Name, string? Path)
{
    public ImageSource? Icon => IconCache.ForFile(Path);
}

/// <summary>Список приложений с окнами — чтобы выбрать, что ограничивать.</summary>
public static class AppCatalog
{
    public static string DisplayName(string process, string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                var d = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
            }
            catch { /* нет доступа к файлу */ }
        }
        return process.Length > 0 ? char.ToUpper(process[0]) + process[1..] : process;
    }

    public static List<RunningApp> Running(IEnumerable<KeyValuePair<string, AppInfo>> known)
    {
        var self = (uint)Environment.ProcessId;
        var result = new Dictionary<string, RunningApp>();
        Win32.EnumWindows((h, _) =>
        {
            if (!Win32.IsWindowVisible(h) || Win32.IsCloaked(h) || Win32.GetWindow(h, Win32.GW_OWNER) != IntPtr.Zero) return true;
            if ((Win32.GetWindowLong(h, Win32.GWL_EXSTYLE) & Win32.WS_EX_TOOLWINDOW) != 0) return true;
            if (Win32.GetTitle(h).Length == 0) return true;
            var pid = Win32.GetPid(h);
            if (pid == self) return true;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                var name = p.ProcessName.ToLowerInvariant();
                if (Matcher.IsProtected(name) || name == "applicationframehost" || result.ContainsKey(name)) return true;
                var path = Win32.GetProcessPath(pid);
                result[name] = new RunningApp(name, DisplayName(name, path), path);
            }
            catch { /* процесс исчез */ }
            return true;
        }, IntPtr.Zero);

        // Плюс то, что уже встречалось в статистике, даже если сейчас не запущено.
        foreach (var (proc, info) in known)
            if (!result.ContainsKey(proc) && !Matcher.IsProtected(proc) && !Matcher.IsBrowser(proc))
                result[proc] = new RunningApp(proc, info.Name, info.Path);

        return result.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

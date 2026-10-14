using System.Windows.Automation;

namespace SelfLock.Services;

/// <summary>
/// Читает адрес открытой страницы из адресной строки браузера через UI Automation (службу специальных возможностей Windows).
/// Поддерживаются Chrome, Edge, Яндекс Браузер, Opera, Brave, Vivaldi и Firefox.
/// </summary>
public sealed class BrowserUrlReader
{
    private readonly Dictionary<IntPtr, AutomationElement> _bars = new();
    private readonly Dictionary<IntPtr, DateTime> _failedAt = new();

    public bool EverSucceeded { get; private set; }

    public string? Read(IntPtr hwnd, string process)
    {
        try
        {
            if (!_bars.TryGetValue(hwnd, out var bar))
            {
                // Поиск по дереву элементов дорогой — после неудачи не повторяем его каждый тик.
                if (_failedAt.TryGetValue(hwnd, out var failed) && DateTime.Now - failed < TimeSpan.FromSeconds(4)) return null;
                bar = Find(hwnd, process);
                if (bar is null)
                {
                    _failedAt[hwnd] = DateTime.Now;
                    return null;
                }
                if (_bars.Count > 64) _bars.Clear();
                _bars[hwnd] = bar;
            }

            if (bar.TryGetCurrentPattern(ValuePattern.Pattern, out var p))
            {
                var value = ((ValuePattern)p).Current.Value;
                if (string.IsNullOrWhiteSpace(value) || value.Contains(' ')) return null; // пусто или в строке набирают поисковый запрос
                EverSucceeded = true;
                return value;
            }
        }
        catch (ElementNotAvailableException) { _bars.Remove(hwnd); }
        catch (Exception) { _bars.Remove(hwnd); _failedAt[hwnd] = DateTime.Now; }
        return null;
    }

    private static AutomationElement? Find(IntPtr hwnd, string process)
    {
        var root = AutomationElement.FromHandle(hwnd);
        if (root is null) return null;

        if (process == "firefox")
        {
            return root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "urlbar-input"));
        }

        // Chromium-браузеры: первое поле ввода с ValuePattern — это омнибокс.
        return root.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true)));
    }
}

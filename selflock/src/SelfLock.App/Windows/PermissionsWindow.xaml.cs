using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SelfLock.Native;
using SelfLock.UI;

namespace SelfLock.Windows;

public sealed record PermissionItem(string Glyph, string Title, string Why, bool? Ok, string? Note = null)
{
    public string Status => Ok switch { true => "✓ доступно", false => Note ?? "недоступно", null => Note ?? "проверится позже" };
    public Brush StatusBg => Visuals.Hex(Ok switch { true => "#163D36", false => "#3D1830", null => "#2A2466" });
    public Brush StatusFg => Visuals.Hex(Ok switch { true => "#34E39A", false => "#FF4D6D", null => "#B9A8FF" });
}

/// <summary>Объясняет, зачем нужен каждый доступ, и проверяет, что он реально работает.</summary>
public partial class PermissionsWindow
{
    private PermissionsWindow()
    {
        InitializeComponent();
        Items.ItemsSource = Check();
    }

    public static void Ask(Window? owner)
    {
        var w = new PermissionsWindow();
        Dialogs.Place(w, owner);
        if (w.ShowDialog() == true) AppHost.Current?.AcceptPermissions();
    }

    public static List<PermissionItem> Check()
    {
        bool Try(Func<bool> f) { try { return f(); } catch { return false; } }

        var procs = Try(() => Process.GetProcesses().Length > 0);
        var fg = Try(() => Win32.GetForegroundWindow() != IntPtr.Zero);
        var uia = Try(() => System.Windows.Automation.AutomationElement.RootElement is not null);
        var url = AppHost.Current?.UrlReadWorked == true;

        return
        [
            new("", "Список процессов", "Узнать, какое приложение запущено, и закрыть его, когда лимит исчерпан.", procs),
            new("", "Активное окно (WinAPI)", "Считать время только тогда, когда вы действительно смотрите на ресурс.", fg),
            new("", "Специальные возможности (UI Automation)", "Прочитать адрес сайта в браузере, чтобы отличить YouTube Shorts от учебного видео.",
                uia && url ? true : uia ? null : false, uia ? "откройте браузер" : null),
            new("", "Окна поверх всех окон", "Показывать предупреждения и экран блокировки поверх браузера и игр.", true),
            new("", "Нажатие Ctrl+W в браузере", "Закрывать вкладку с заблокированным сайтом, не трогая остальные.", true),
        ];
    }

    private void Allow(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Later(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Drag(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}

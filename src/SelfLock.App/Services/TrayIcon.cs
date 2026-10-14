using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using SelfLock.Native;

namespace SelfLock.Services;

/// <summary>Иконка в области уведомлений (возле часов) с тёмным меню по правому клику.</summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 77; // WM_APP + 77
    private const int WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203, WM_RBUTTONUP = 0x205;

    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;
    private IntPtr _icon;
    private Win32.NOTIFYICONDATA _data;

    public event Action? Open;
    public event Action<ContextMenu>? MenuOpening;

    public ContextMenu Menu { get; } = new();

    public TrayIcon(string tooltip)
    {
        _source = new HwndSource(new HwndSourceParameters("SelfLockTray") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
        _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");

        // Иконка зашита в сам exe (ApplicationIcon) — берём её оттуда, так работает и однофайловая сборка.
        var small = new IntPtr[1];
        if (Environment.ProcessPath is { } exe && Win32.ExtractIconEx(exe, 0, null, small, 1) > 0)
            _icon = small[0];

        _data = new Win32.NOTIFYICONDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
            hWnd = _source.Handle,
            uID = 1,
            uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = tooltip,
            szInfo = "",
            szInfoTitle = "",
        };
        Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref _data);
    }

    public void SetTooltip(string text)
    {
        if (text.Length > 120) text = text[..120];
        if (_data.szTip == text) return;
        _data.szTip = text;
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref _data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreated)
        {
            // Проводник перезапустился — возвращаем иконку на место.
            Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref _data);
        }
        else if (msg == CallbackMessage)
        {
            switch ((int)lParam & 0xFFFF)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    Open?.Invoke();
                    break;
                case WM_RBUTTONUP:
                    ShowMenu();
                    break;
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        MenuOpening?.Invoke(Menu);
        // Без этого меню не закроется по клику мимо.
        Win32.SetForegroundWindow(_source.Handle);
        Menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        Menu.IsOpen = true;
    }

    public void Dispose()
    {
        Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref _data);
        if (_icon != IntPtr.Zero) Win32.DestroyIcon(_icon);
        _icon = IntPtr.Zero;
        _source.Dispose();
    }
}

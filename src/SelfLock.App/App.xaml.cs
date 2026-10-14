using System.Windows;
using SelfLock.Core;

namespace SelfLock;

public partial class App
{
    private Mutex? _single;
    private EventWaitHandle? _showSignal;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log(ex);
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Второй запуск просто показывает уже работающий SelfLock.
        _single = new Mutex(true, @"Local\SelfLock.SingleInstance", out var first);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting(@"Local\SelfLock.Show").Set(); } catch { }
            Shutdown();
            return;
        }

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SelfLock.Show");
        new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.BeginInvoke(() => AppHost.Current?.ShowMain());
        }) { IsBackground = true }.Start();

        DispatcherUnhandledException += (_, ex) =>
        {
            // Ошибка в интерфейсе не должна снимать защиту — логируем и продолжаем работать.
            Log(ex.Exception);
            ex.Handled = true;
        };

        var dataDir = Environment.GetEnvironmentVariable("SELFLOCK_DATA") is { Length: > 0 } d ? d : Store.DefaultDirectory;
        var host = new AppHost(new Store(dataDir));
        host.Start(startHidden: e.Args.Contains("--tray"));
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        AppHost.Current?.SaveAll();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppHost.Current?.Shutdown();
        base.OnExit(e);
    }

    public static void Log(Exception ex)
    {
        try
        {
            var dir = AppHost.Current?.Store.Directory
                      ?? (Environment.GetEnvironmentVariable("SELFLOCK_DATA") is { Length: > 0 } d ? d : Store.DefaultDirectory);
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "errors.log"), $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch { }
    }
}

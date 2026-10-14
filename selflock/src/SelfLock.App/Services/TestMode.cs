namespace SelfLock.Services;

/// <summary>
/// Режим для QA-тестирования (переменные окружения, в обычной работе не задаются):
/// SELFLOCK_DATA       — папка с данными вместо %APPDATA%\SelfLock;
/// SELFLOCK_FAKE_FG    — «активное окно» задаётся вручную: "процесс" или "процесс|адрес";
/// SELFLOCK_OFFSCREEN  — =1: предупреждения и экран блокировки выводятся за пределами экрана и не забирают фокус.
/// </summary>
public static class TestMode
{
    public static readonly string? FakeForeground = Env("SELFLOCK_FAKE_FG");
    public static readonly bool Offscreen = Env("SELFLOCK_OFFSCREEN") == "1";

    public const double OffscreenLeft = -30000;

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}

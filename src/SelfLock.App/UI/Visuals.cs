using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SelfLock.Core;
using SelfLock.Services;

namespace SelfLock.UI;

public sealed record SitePreset(string Name, string Pattern, string Color, string Letter, bool HardByDefault = false);

/// <summary>Как выглядит ресурс в списках: иконка exe или цветной значок сайта.</summary>
public sealed record Look(ImageSource? Image, Brush Background, string Letter);

public static class Visuals
{
    public static readonly SitePreset[] Sites =
    [
        new("YouTube", "youtube.com", "#FF1E3C", "▶"),
        new("YouTube Shorts", "youtube.com/shorts", "#FF1E3C", "S", HardByDefault: true),
        new("TikTok", "tiktok.com", "#14141C", "♪", HardByDefault: true),
        new("VK", "vk.com", "#0077FF", "VK"),
        new("VK Клипы", "vk.com/clips", "#0077FF", "VK", HardByDefault: true),
        new("Instagram", "instagram.com", "#E1306C", "IG"),
        new("Twitch", "twitch.tv", "#9146FF", "T"),
        new("Reddit", "reddit.com", "#FF4500", "R"),
        new("X", "x.com", "#16161E", "X"),
        new("Pinterest", "pinterest.com", "#E60023", "P"),
        new("Одноклассники", "ok.ru", "#EE8208", "OK"),
        new("Дзен", "dzen.ru", "#16161E", "Д"),
        new("Rutube", "rutube.ru", "#2C2CC8", "R"),
    ];

    private static readonly string[] Palette = ["#7C5CFF", "#4F8BFF", "#20C3A8", "#FF7A59", "#E85CC5", "#FFB648", "#3DB8FF"];

    public static SitePreset? SitePresetFor(string pattern)
    {
        var p = Matcher.NormalizeUrl(pattern);
        return Sites.FirstOrDefault(s => s.Pattern == p)
               ?? Sites.FirstOrDefault(s => !s.Pattern.Contains('/') && Matcher.UrlMatches(s.Pattern, p));
    }

    public static Brush Hex(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private static Brush HashColor(string s)
    {
        var h = 0;
        foreach (var c in s) h = h * 31 + c;
        return Hex(Palette[Math.Abs(h) % Palette.Length]);
    }

    public static Look ForSite(string pattern)
    {
        if (SitePresetFor(pattern) is { } p) return new Look(null, Hex(p.Color), p.Letter);
        var hint = Matcher.TitleHint(pattern);
        return new Look(null, HashColor(hint), hint.Length > 0 ? char.ToUpper(hint[0]).ToString() : "•");
    }

    public static Look ForApp(string process, string? path)
    {
        var img = IconCache.ForFile(path);
        return new Look(img, img is null ? HashColor(process) : Hex("#1B2143"),
            process.Length > 0 ? char.ToUpper(process[0]).ToString() : "?");
    }

    public static Look ForRule(Rule r) => r.Kind == TargetKind.Site ? ForSite(r.Target) : ForApp(r.Target, r.ExePath);

    /// <summary>Значок для ключа статистики "site:..." / "app:...".</summary>
    public static Look ForUsageKey(string key, IReadOnlyDictionary<string, AppInfo> apps)
    {
        if (key.StartsWith("site:")) return ForSite(key[5..]);
        var proc = key[4..];
        return ForApp(proc, apps.TryGetValue(proc, out var a) ? a.Path : null);
    }

    public static string NameForUsageKey(string key, IReadOnlyDictionary<string, AppInfo> apps)
    {
        if (key.StartsWith("site:"))
        {
            var host = key[5..];
            return SitePresetFor(host) is { } p && !p.Pattern.Contains('/') ? p.Name : host;
        }
        var proc = key[4..];
        return apps.TryGetValue(proc, out var a) && a.Name.Length > 0 ? a.Name : proc;
    }
}

/// <summary>Квадратный значок ресурса (иконка приложения или буква сайта на цветном фоне).</summary>
public sealed class ResourceBadge : Border
{
    public static readonly DependencyProperty LookProperty = DependencyProperty.Register(
        nameof(Look), typeof(Look), typeof(ResourceBadge), new PropertyMetadata(null, (d, _) => ((ResourceBadge)d).Rebuild()));

    public Look? Look
    {
        get => (Look?)GetValue(LookProperty);
        set => SetValue(LookProperty, value);
    }

    public ResourceBadge()
    {
        Width = Height = 38;
        CornerRadius = new CornerRadius(10);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Rebuild();
    }

    private void Rebuild()
    {
        if (Look is not { } look) { Child = null; return; }
        Background = look.Background;
        var size = double.IsNaN(Width) ? 38 : Width;
        if (look.Image is { } img)
        {
            Child = new Image { Source = img, Width = size * 0.62, Height = size * 0.62 };
            RenderOptions.SetBitmapScalingMode(Child, BitmapScalingMode.HighQuality);
        }
        else
        {
            Child = new TextBlock
            {
                Text = look.Letter,
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = look.Letter.Length > 1 ? size * 0.32 : size * 0.42,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
    }
}

/// <summary>Кольцевой индикатор (карточка «Сегодня»).</summary>
public sealed class Ring : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Ring), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(Ring), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    private static readonly Brush Track = Visuals.Hex("#212748");
    private static readonly Brush Fill = new LinearGradientBrush(
        (Color)ColorConverter.ConvertFromString("#A35CFF"), (Color)ColorConverter.ConvertFromString("#4F8BFF"), 45);

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = (size - Thickness) / 2;
        dc.DrawEllipse(null, new Pen(Track, Thickness), c, r, r);

        var v = Math.Clamp(Value, 0, 1);
        if (v <= 0.001) return;
        var pen = new Pen(Fill, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (v >= 0.999)
        {
            dc.DrawEllipse(null, pen, c, r, r);
            return;
        }
        var angle = v * 360;
        Point At(double deg) => new(c.X + r * Math.Sin(deg * Math.PI / 180), c.Y - r * Math.Cos(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(At(0), false, false);
            ctx.ArcTo(At(angle), new Size(r, r), 0, angle > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>Числовое поле со стрелками − / + (без выпадающих списков).</summary>
public sealed class Stepper : Border
{
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 14, FontWeight = FontWeights.SemiBold };
    private int _value;

    public int Minimum { get; set; } = 1;
    public int Maximum { get; set; } = 600;
    public int Step { get; set; } = 1;
    public string Unit { get; set; } = "мин";
    public Func<int, string>? Formatter { get; set; }

    public event Action<int>? ValueChanged;

    public int Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(value, Minimum, Maximum);
            if (v == _value && _text.Text.Length > 0) return;
            _value = v;
            _text.Text = Formatter?.Invoke(v) ?? $"{v} {Unit}";
            ValueChanged?.Invoke(v);
        }
    }

    public Stepper()
    {
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        Background = (Brush)Application.Current.FindResource("Bg1");
        BorderBrush = (Brush)Application.Current.FindResource("Stroke");
        _text.Foreground = (Brush)Application.Current.FindResource("Text");
        Height = 40;
        MinWidth = 170;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        var minus = MakeButton("", -1);
        var plus = MakeButton("", +1);
        Grid.SetColumn(_text, 1);
        Grid.SetColumn(plus, 2);
        grid.Children.Add(minus);
        grid.Children.Add(_text);
        grid.Children.Add(plus);
        Child = grid;

        MouseWheel += (_, e) => Value += Math.Sign(e.Delta) * StepFor(Math.Sign(e.Delta));
    }

    /// <summary>Шаг крупнее на больших значениях: 1…10 по 1, дальше по 5, после 60 — по 15.</summary>
    private int StepFor(int dir)
    {
        if (Step != 1) return Step;
        var v = dir > 0 ? _value : _value - 1;
        return v >= 120 ? 15 : v >= 10 ? 5 : 1;
    }

    private System.Windows.Controls.Primitives.RepeatButton MakeButton(string glyph, int dir)
    {
        var b = new System.Windows.Controls.Primitives.RepeatButton
        {
            Content = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 12 },
            Delay = 350,
            Interval = 70,
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            Foreground = (Brush)Application.Current.FindResource("Muted"),
            Template = RepeatTemplate(),
        };
        b.Click += (_, _) =>
        {
            var step = StepFor(dir);
            // выравниваем на сетку шага: 12 → 15, а не 17
            var next = _value + dir * step;
            if (step > 1) next = dir > 0 ? next / step * step : (int)Math.Ceiling(next / (double)step) * step;
            Value = next;
        };
        return b;
    }

    private static ControlTemplate RepeatTemplate()
    {
        var t = new ControlTemplate(typeof(System.Windows.Controls.Primitives.RepeatButton));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        bd.Name = "bd";
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        t.VisualTree = bd;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Visuals.Hex("#222A52"), "bd"));
        t.Triggers.Add(hover);
        return t;
    }
}

public static class Ru
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("ru-RU");
}

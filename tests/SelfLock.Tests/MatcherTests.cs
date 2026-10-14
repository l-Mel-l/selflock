using SelfLock.Core;

namespace SelfLock.Tests;

public class MatcherTests
{
    private static ForegroundInfo Browser(string url, string title = "") => new("chrome", title, url, true);

    [Theory]
    [InlineData("youtube.com", "https://www.youtube.com/watch?v=1", true)]
    [InlineData("youtube.com", "music.youtube.com/", true)]
    [InlineData("youtube.com", "notyoutube.com", false)]
    [InlineData("youtube.com/shorts", "https://www.youtube.com/shorts/abc", true)]
    [InlineData("youtube.com/shorts", "https://www.youtube.com/watch?v=1", false)]
    [InlineData("https://VK.com/", "vk.com/feed", true)]
    [InlineData("tiktok.com", "m.tiktok.com/@user", true)]
    public void Url_matching(string pattern, string url, bool expected) =>
        Assert.Equal(expected, Matcher.UrlMatches(pattern, url));

    [Fact]
    public void App_rule_matches_process_name_case_insensitive()
    {
        var rule = new Rule { Kind = TargetKind.App, Target = "Telegram.exe" };
        Assert.True(Matcher.Matches(rule, new ForegroundInfo("telegram", "Telegram", null, false)));
        Assert.False(Matcher.Matches(rule, new ForegroundInfo("discord", "Discord", null, false)));
    }

    [Fact]
    public void Site_rule_never_matches_non_browser()
    {
        var rule = new Rule { Kind = TargetKind.Site, Target = "youtube.com" };
        Assert.False(Matcher.Matches(rule, new ForegroundInfo("notepad", "youtube.com - Блокнот", null, false)));
    }

    [Fact]
    public void Site_falls_back_to_window_title_when_url_unknown()
    {
        var rule = new Rule { Kind = TargetKind.Site, Target = "youtube.com" };
        Assert.True(Matcher.Matches(rule, new ForegroundInfo("msedge", "Котики - YouTube — Microsoft Edge", null, true)));
    }

    [Fact]
    public void Shorts_rule_does_not_guess_by_title()
    {
        var rule = new Rule { Kind = TargetKind.Site, Target = "youtube.com/shorts" };
        Assert.False(Matcher.Matches(rule, new ForegroundInfo("chrome", "Котики - YouTube", null, true)));
        Assert.True(Matcher.Matches(rule, Browser("youtube.com/shorts/xyz")));
    }

    [Fact]
    public void Usage_key_prefers_site_host()
    {
        Assert.Equal("site:youtube.com", Matcher.UsageKey(Browser("https://www.youtube.com/watch")));
        Assert.Equal("app:chrome", Matcher.UsageKey(new ForegroundInfo("chrome", "Новая вкладка", null, true)));
        Assert.Equal("app:code", Matcher.UsageKey(new ForegroundInfo("Code", "x", null, false)));
    }

    [Fact]
    public void System_processes_are_protected()
    {
        Assert.True(Matcher.IsProtected("explorer.exe"));
        Assert.True(Matcher.IsProtected("SelfLock"));
        Assert.False(Matcher.IsProtected("steam"));
    }
}

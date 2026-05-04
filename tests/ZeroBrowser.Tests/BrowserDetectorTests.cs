using FluentAssertions;
using Xunit;
using ZeroBrowser.Browser;

namespace ZeroBrowser.Tests;

public class BrowserDetectorTests
{
    [Fact]
    public void Detect_does_not_throw_on_any_platform()
    {
        // The result depends entirely on what the host happens to have
        // installed; CI runners may or may not have Chrome / Brave / Edge.
        // We only assert that the API is safe to call.
        var act = () => BrowserDetector.Detect();
        act.Should().NotThrow();
    }

    [Fact]
    public void Detect_returns_only_browsers_whose_files_exist()
    {
        var browsers = BrowserDetector.Detect();
        foreach (var b in browsers)
        {
            File.Exists(b.Path).Should().BeTrue($"{b.DisplayName} reported path '{b.Path}' but file doesn't exist");
        }
    }

    [Fact]
    public void Detect_returns_each_path_at_most_once()
    {
        var browsers = BrowserDetector.Detect();
        browsers.Select(b => b.Path).Should().OnlyHaveUniqueItems();
    }
}

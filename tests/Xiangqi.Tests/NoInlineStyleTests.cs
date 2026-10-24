using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Xiangqi.Tests;

/// <summary>
/// The CSP is style-src 'self' (no 'unsafe-inline'), so any inline style attribute / style
/// element / setAttribute("style") / cssText written by our own assets would be blocked and
/// log "Applying inline style violates ..." in the browser console.
/// </summary>
public class NoInlineStyleTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public NoInlineStyleTests(WebApplicationFactory<Program> factory)
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
        _factory = factory;
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/app.js")]
    [InlineData("/continue.js")]
    [InlineData("/lib/signalr.min.js")]
    public async Task Asset_HasNoInlineStyleSources(string path)
    {
        using HttpClient client = _factory.CreateClient();
        string body = await client.GetStringAsync(path);

        Assert.DoesNotMatch(new Regex(@"<style[\s>]", RegexOptions.IgnoreCase), body);
        Assert.DoesNotMatch(new Regex(@"\sstyle\s*=", RegexOptions.IgnoreCase), body);
        Assert.DoesNotMatch(new Regex(@"setAttribute\(\s*[""']style[""']"), body);
        Assert.DoesNotMatch(new Regex(@"\.style\.cssText|\.style\s*=(?!=)|""style""\s*:"), body);
        Assert.DoesNotMatch(new Regex(@"insertAdjacentHTML|\.innerHTML\s*="), body);
    }
}

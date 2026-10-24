using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Xiangqi.Tests;

public class SecurityHeadersTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SecurityHeadersTests(WebApplicationFactory<Program> factory)
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
        _factory = factory;
    }

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/")]
    [InlineData("/no-such-path-security-headers")]
    public async Task Responses_CarryBaselineSecurityHeaders(string path)
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("camera=(), microphone=(), geolocation=()", Header(response, "Permissions-Policy"));

        string csp = Header(response, "Content-Security-Policy");
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("script-src 'self';", csp);
        Assert.DoesNotContain("cdnjs", csp);
        Assert.DoesNotContain("googleapis", csp);
        Assert.Contains("ws://localhost", csp);

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
        Assert.False(response.Headers.Contains("Server"));
        Assert.False(response.Headers.Contains("X-Powered-By"));
    }

    [Fact]
    public async Task Https_SendsHstsWithoutPreload()
    {
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });
        using HttpResponseMessage response = await client.GetAsync("/api/health");

        Assert.Equal("max-age=15552000", Header(response, "Strict-Transport-Security"));
        string csp = Header(response, "Content-Security-Policy");
        Assert.Contains("wss://localhost", csp);
    }

    [Fact]
    public void IndexHtml_ScriptsStayCompatibleWithCsp()
    {
        string html = File.ReadAllText(FindIndexHtml());

        Assert.DoesNotContain(" style=\"", html, StringComparison.Ordinal);

        foreach (Match script in Regex.Matches(html, @"<script\b([^>]*)>", RegexOptions.IgnoreCase))
        {
            string attrs = script.Groups[1].Value;
            Match src = Regex.Match(attrs, @"\bsrc\s*=\s*([""'])(.*?)\1", RegexOptions.IgnoreCase);
            Assert.True(src.Success, "inline <script> without src is not allowed");
            string url = src.Groups[2].Value;
            Assert.True(
                url.StartsWith("/", StringComparison.Ordinal)
                || url.StartsWith("https://cdnjs.cloudflare.com/", StringComparison.Ordinal),
                url);
        }
    }

    private static string Header(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out IEnumerable<string>? values))
            return string.Join(", ", values);
        if (response.Content.Headers.TryGetValues(name, out values))
            return string.Join(", ", values);
        Assert.Fail($"missing header {name}");
        return "";
    }

    private static string FindIndexHtml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "Xiangqi.Server", "wwwroot", "index.html");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("src/Xiangqi.Server/wwwroot/index.html");
    }
}

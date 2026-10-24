using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xiangqi.Server;
using Xunit;

namespace Xiangqi.Tests;

public class BuildInfoTests
{
    [Theory]
    [InlineData("1.0.0+abcdef1234567890", "abcdef123456")]
    [InlineData("1.0.0+ABCDEF1", "abcdef1")]
    [InlineData("1.0.0", "unknown")]
    [InlineData("1.0.0+", "unknown")]
    [InlineData("1.0.0+unknown", "unknown")]
    [InlineData("1.0.0+xyz1234", "unknown")]
    [InlineData("1.0.0+abc", "unknown")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    public void ParseSha(string? informationalVersion, string expected) =>
        Assert.Equal(expected, BuildInfo.ParseSha(informationalVersion));

    [Fact]
    public void FromAssembly_version_and_builtAt()
    {
        var info = BuildInfo.FromAssembly(typeof(BuildInfo).Assembly);
        AssertVersion(info.Version);
        AssertBuiltAt(info.BuiltAt);
    }

    internal static void AssertVersion(string version) =>
        Assert.True(version == BuildInfo.Unknown || Regex.IsMatch(version, "^[0-9a-f]{7,12}$"));

    internal static void AssertBuiltAt(string builtAt) =>
        Assert.True(builtAt == BuildInfo.Unknown || DateTimeOffset.TryParse(
            builtAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _));
}

public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Get_api_health()
    {
        var response = await _client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());

        var version = StringProp(root, "version");
        var builtAt = StringProp(root, "builtAt");
        BuildInfoTests.AssertVersion(version);
        BuildInfoTests.AssertBuiltAt(builtAt);
        Assert.Equal(BuildInfo.FromAssembly(typeof(BuildInfo).Assembly).Version, version);
    }

    private static string StringProp(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        return value.GetString()!;
    }
}

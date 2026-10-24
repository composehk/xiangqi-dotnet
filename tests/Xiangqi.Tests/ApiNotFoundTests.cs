using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Xiangqi.Tests;

public class ApiNotFoundTests
{
    public ApiNotFoundTests() => Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");

    [Fact]
    public async Task UnknownApiPath_IsBilingual404()
    {
        using WebApplicationFactory<Program> factory = new();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/api/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        string error = await Error(response);
        Assert.Contains("接口不存在", error);
        Assert.Contains("Not found.", error);
    }

    [Fact]
    public async Task RoomsWrongMethod_IsBilingual405AndKeepsAllow()
    {
        using WebApplicationFactory<Program> factory = new();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.DeleteAsync("/api/rooms");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        bool allowsGet = response.Content.Headers.Allow.Contains("GET")
            || (response.Headers.TryGetValues("Allow", out IEnumerable<string>? allow)
                && allow.Any(value => value.Contains("GET", StringComparison.Ordinal)));
        Assert.True(allowsGet);
        string error = await Error(response);
        Assert.Contains("不支持的请求方法", error);
        Assert.Contains("Method not allowed.", error);
    }

    [Fact]
    public async Task UnknownRoom_KeepsService404Body()
    {
        using WebApplicationFactory<Program> factory = new();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync($"/api/rooms/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        string error = await Error(response);
        Assert.Equal("房间不存在 / Room not found.", error);
    }

    private static async Task<string> Error(HttpResponseMessage response)
    {
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetString()!;
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Xiangqi.Tests;

public class RetryAfterTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public RetryAfterTests() => Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");

    [Fact]
    public async Task SuccessiveRejections_RetryAfterIsRemainingWindowAndNonIncreasing()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Payment:CreatePerMinute", "1");
        });
        using HttpClient client = factory.CreateClient();
        var body = new { moves = "炮二平五 马8进7 马二进三", ply = 2, difficulty = "easy", humanColor = "red" };

        using HttpResponseMessage first = await client.PostAsJsonAsync("/api/continue/sessions", body, Json);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);

        int? previous = null;
        for (int i = 0; i < 3; i++)
        {
            using HttpResponseMessage limited = await client.PostAsJsonAsync("/api/continue/sessions", body, Json);
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.True(limited.Headers.TryGetValues("Retry-After", out IEnumerable<string>? values));
            int retry = int.Parse(values!.Single(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(retry, 1, 60);
            using JsonDocument doc = JsonDocument.Parse(await limited.Content.ReadAsStringAsync());
            string error = doc.RootElement.GetProperty("error").GetString()!;
            Assert.Contains(retry.ToString(System.Globalization.CultureInfo.InvariantCulture), error);
            Assert.Contains("请求过于频繁", error);
            if (previous is not null)
                Assert.True(retry <= previous);
            previous = retry;
        }
    }
}

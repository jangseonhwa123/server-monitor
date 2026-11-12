using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Server.Tests;

[Collection("api")]
public class QueryTests
{
    private readonly ApiFactory _factory;

    public QueryTests(ApiFactory factory) => _factory = factory;

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        return client;
    }

    private static string? Str(JsonElement item, string name) =>
        item.EnumerateObject()
            .First(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            .Value.GetString();

    private static async Task<bool> WaitUntil(Func<Task<bool>> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return true;
            await Task.Delay(50);
        }
        return false;
    }

    [Theory]
    [InlineData("/api/metrics?limit=-1")]
    [InlineData("/api/metrics?limit=100000")]
    [InlineData("/api/alerts?limit=0")]
    [InlineData("/api/devices/anything/history?limit=-5")]
    public async Task Out_of_range_limits_are_clamped_instead_of_failing(string url)
    {
        var response = await Client().GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Device_names_are_case_sensitive()
    {
        var client = Client();
        await client.PostAsJsonAsync("/api/metrics", new { deviceName = "T-Case", cpu = 10, memory = 10, timestamp = DateTime.UtcNow });
        await client.PostAsJsonAsync("/api/metrics", new { deviceName = "t-case", cpu = 20, memory = 20, timestamp = DateTime.UtcNow });

        var bothListed = await WaitUntil(async () =>
        {
            var devices = await client.GetFromJsonAsync<JsonElement>("/api/devices");
            var names = devices.EnumerateArray().Select(d => Str(d, "deviceName")).ToList();
            return names.Contains("T-Case") && names.Contains("t-case");
        });

        Assert.True(bothListed, "대소문자가 다른 두 장비가 따로 보여야 합니다");
    }

    [Fact]
    public async Task Timestamps_are_returned_in_utc_with_microsecond_precision()
    {
        var client = Client();
        var sent = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560);
        await client.PostAsJsonAsync("/api/metrics", new { deviceName = "t-utc", cpu = 10, memory = 10, timestamp = sent });

        JsonElement history = default;
        var stored = await WaitUntil(async () =>
        {
            history = await client.GetFromJsonAsync<JsonElement>("/api/devices/t-utc/history?limit=10");
            return history.GetArrayLength() >= 1;
        });
        Assert.True(stored, "지표가 시간 안에 저장되지 않았습니다");

        var text = Str(history[0], "timestamp")!;
        Assert.EndsWith("Z", text);
        var parsed = DateTimeOffset.Parse(text).UtcDateTime;
        Assert.True((parsed - sent).Duration() < TimeSpan.FromMilliseconds(1), $"저장 전후 시각이 다릅니다: {text}");
    }
}

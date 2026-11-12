using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Server.Tests;

public class ApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ApiTests(ApiFactory factory) => _factory = factory;

    private HttpClient AuthedClient(string key = ApiFactory.ApiKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private static object Metric(string device, double cpu, double memory = 50) =>
        new { deviceName = device, cpu, memory, timestamp = DateTime.UtcNow };

    private static string? Str(JsonElement item, string name) =>
        item.EnumerateObject()
            .First(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            .Value.GetString();

    // 지표 저장은 큐를 거쳐 비동기로 이루어지므로 잠깐 기다리며 확인합니다
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

    [Fact]
    public async Task Root_returns_running_message()
    {
        var response = await _factory.CreateClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("running", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_without_api_key_is_rejected()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/metrics", Metric("t-nokey", 10));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_with_wrong_api_key_is_rejected()
    {
        var response = await AuthedClient("wrong-key").PostAsJsonAsync("/api/metrics", Metric("t-wrongkey", 10));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_with_valid_api_key_is_accepted()
    {
        var response = await AuthedClient().PostAsJsonAsync("/api/metrics", Metric("t-valid", 10));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Posted_metric_is_stored_and_device_is_listed()
    {
        var client = AuthedClient();
        await client.PostAsJsonAsync("/api/metrics", Metric("t-store", 33));

        var stored = await WaitUntil(async () =>
        {
            var history = await client.GetFromJsonAsync<JsonElement>("/api/devices/t-store/history?limit=10");
            return history.GetArrayLength() >= 1;
        });
        Assert.True(stored, "지표가 시간 안에 저장되지 않았습니다");

        var devices = await client.GetFromJsonAsync<JsonElement>("/api/devices");
        Assert.Contains(devices.EnumerateArray(), d => Str(d, "deviceName") == "t-store");
    }

    [Fact]
    public async Task High_cpu_raises_only_one_alert_within_cooldown()
    {
        var client = AuthedClient();
        await client.PostAsJsonAsync("/api/metrics", Metric("t-alert-high", 95));
        await client.PostAsJsonAsync("/api/metrics", Metric("t-alert-high", 96));

        var alerts = await client.GetFromJsonAsync<JsonElement>("/api/alerts?limit=100");
        var count = alerts.EnumerateArray().Count(a => Str(a, "deviceName") == "t-alert-high");

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Normal_cpu_raises_no_alert()
    {
        var client = AuthedClient();
        await client.PostAsJsonAsync("/api/metrics", Metric("t-alert-none", 10));

        var alerts = await client.GetFromJsonAsync<JsonElement>("/api/alerts?limit=100");
        var count = alerts.EnumerateArray().Count(a => Str(a, "deviceName") == "t-alert-none");

        Assert.Equal(0, count);
    }
}

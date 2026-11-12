using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Server.Tests;

[Collection("api")]
public class ValidationTests
{
    private readonly ApiFactory _factory;

    public ValidationTests(ApiFactory factory) => _factory = factory;

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        return client;
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string device, double cpu, double memory, int minutesFromNow = 0) =>
        client.PostAsJsonAsync("/api/metrics",
            new { deviceName = device, cpu, memory, timestamp = DateTime.UtcNow.AddMinutes(minutesFromNow) });

    [Theory]
    [InlineData("t-bad-cpu-high", 101.0, 50.0, 0)]
    [InlineData("t-bad-cpu-low", -1.0, 50.0, 0)]
    [InlineData("t-bad-mem-high", 50.0, 100.5, 0)]
    [InlineData("t-bad-mem-low", 50.0, -0.1, 0)]
    [InlineData("t-bad-future", 50.0, 50.0, 1440)]
    public async Task Invalid_values_are_rejected(string device, double cpu, double memory, int minutesFromNow)
    {
        var response = await Post(Client(), device, cpu, memory, minutesFromNow);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_device_name_is_rejected(string name)
    {
        var response = await Post(Client(), name, 10, 10);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Too_long_device_name_is_rejected()
    {
        var response = await Post(Client(), new string('x', 65), 10, 10);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Boundary_values_are_accepted()
    {
        var client = Client();

        Assert.Equal(HttpStatusCode.OK, (await Post(client, "t-boundary", 0, 100)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(client, new string('y', 64), 5, 5)).StatusCode);
    }

    [Fact]
    public async Task Old_timestamps_are_accepted_for_buffered_resend()
    {
        var response = await Post(Client(), "t-old", 10, 10, minutesFromNow: -600);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Rejected_metric_is_not_stored()
    {
        var client = Client();
        var response = await Post(client, "t-not-stored", 101, 50);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await Task.Delay(500); // 저장이 비동기라 충분히 기다린 뒤 확인합니다
        var devices = await client.GetFromJsonAsync<JsonElement>("/api/devices");
        var found = devices.EnumerateArray().Any(d =>
            d.EnumerateObject().Any(p =>
                string.Equals(p.Name, "deviceName", StringComparison.OrdinalIgnoreCase) &&
                p.Value.GetString() == "t-not-stored"));

        Assert.False(found);
    }
}

using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Dashboard.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace Dashboard.Services;

public enum ConnectionState { Connecting, Connected, Reconnecting, Disconnected }

// 서버와의 통신(REST 조회 + SignalR 실시간 수신 + 자동 재연결)만 담당합니다
public class MonitorClient
{
    private readonly HttpClient _http;
    private readonly HubConnection _hub;

    public event Action<MetricPush>? MetricReceived;
    public event Action<AlertDto>? AlertRaised;
    public event Action? Connected; // 최초 연결 또는 재연결이 끝났을 때

    public ConnectionState State { get; private set; } = ConnectionState.Connecting;

    public MonitorClient(string baseUrl)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(5) };
        _hub = new HubConnectionBuilder()
            .WithUrl($"{baseUrl}/hub/monitor")
            .WithAutomaticReconnect()
            .Build();

        _hub.On<MetricPush>("MetricReceived", m => MetricReceived?.Invoke(m));
        _hub.On<AlertDto>("AlertRaised", a => AlertRaised?.Invoke(a));

        _hub.Reconnecting += error =>
        {
            State = ConnectionState.Reconnecting;
            return Task.CompletedTask;
        };
        _hub.Reconnected += connectionId =>
        {
            State = ConnectionState.Connected;
            Connected?.Invoke();
            return Task.CompletedTask;
        };
        _hub.Closed += error =>
        {
            State = ConnectionState.Disconnected;
            _ = ConnectLoopAsync();
            return Task.CompletedTask;
        };
    }

    public Task StartAsync() => ConnectLoopAsync();

    private async Task ConnectLoopAsync()
    {
        State = ConnectionState.Connecting;
        while (true)
        {
            try
            {
                await _hub.StartAsync();
                State = ConnectionState.Connected;
                Connected?.Invoke();
                return;
            }
            catch
            {
                State = ConnectionState.Disconnected;
                await Task.Delay(2000);
            }
        }
    }

    public async Task<List<DeviceDto>> GetDevicesAsync() =>
        await _http.GetFromJsonAsync<List<DeviceDto>>("/api/devices") ?? new();

    public async Task<List<AlertDto>> GetAlertsAsync(int limit) =>
        await _http.GetFromJsonAsync<List<AlertDto>>($"/api/alerts?limit={limit}") ?? new();

    public async Task<List<HistoryDto>> GetHistoryAsync(string device, int limit) =>
        await _http.GetFromJsonAsync<List<HistoryDto>>(
            $"/api/devices/{Uri.EscapeDataString(device)}/history?limit={limit}") ?? new();
}

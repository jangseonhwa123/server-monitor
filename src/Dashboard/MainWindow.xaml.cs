using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.AspNetCore.SignalR.Client;

namespace Dashboard;

public partial class MainWindow : Window
{
    private const string BaseUrl = "http://localhost:5000";
    private const int MaxPoints = 60;
    private const int MaxAlerts = 20;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly HubConnection _hub;

    private readonly Dictionary<string, DeviceState> _devices = new();
    private readonly List<AlertRow> _alerts = new(); // 최신 알람이 앞
    private List<HistoryDto> _history = new();
    private string? _selectedDevice;

    private bool _refreshing;
    private bool _dirty = true;
    private bool _alertsDirty = true;
    private DateTime _lastRender = DateTime.MinValue;
    private string _connectionText = "연결 중...";

    public MainWindow()
    {
        InitializeComponent();

        _hub = new HubConnectionBuilder()
            .WithUrl($"{BaseUrl}/hub/monitor")
            .WithAutomaticReconnect()
            .Build();

        // 서버가 밀어 주는 이벤트
        _hub.On<MetricPush>("MetricReceived", m => Dispatcher.Invoke(() => OnMetric(m)));
        _hub.On<AlertDto>("AlertRaised", a => Dispatcher.Invoke(() => OnAlert(a)));

        _hub.Reconnecting += _ =>
        {
            _connectionText = "재연결 중...";
            return Task.CompletedTask;
        };
        _hub.Reconnected += async _ =>
        {
            _connectionText = "실시간 연결됨 (SignalR)";
            await LoadSnapshotAsync(); // 끊긴 동안 놓친 데이터를 다시 불러옵니다
        };
        _hub.Closed += closedError =>
        {
            _connectionText = "연결 끊김 · 재시도 중...";
            _ = ConnectLoopAsync();
            return Task.CompletedTask;
        };

        _uiTimer.Tick += (_, _) =>
        {
            if (_dirty || _alertsDirty || (DateTime.Now - _lastRender).TotalSeconds >= 1) Render();
        };
        _uiTimer.Start();

        _ = ConnectLoopAsync();
    }

    private async Task ConnectLoopAsync()
    {
        while (true)
        {
            try
            {
                await _hub.StartAsync();
                _connectionText = "실시간 연결됨 (SignalR)";
                await LoadSnapshotAsync();
                return;
            }
            catch
            {
                _connectionText = "서버 연결 실패 · 재시도 중...";
                await Task.Delay(2000);
            }
        }
    }

    // 처음 연결(또는 재연결) 때 한 번만 REST로 현재 상태를 불러옵니다
    private async Task LoadSnapshotAsync()
    {
        try
        {
            var devices = await _http.GetFromJsonAsync<List<DeviceDto>>($"{BaseUrl}/api/devices") ?? new();
            var alerts = await _http.GetFromJsonAsync<List<AlertDto>>($"{BaseUrl}/api/alerts?limit={MaxAlerts}") ?? new();

            await Dispatcher.InvokeAsync(() =>
            {
                _devices.Clear();
                foreach (var d in devices)
                {
                    _devices[d.DeviceName] = new DeviceState(d.DeviceName, d.Cpu, d.Memory, d.LastSeen);
                }

                _alerts.Clear();
                _alerts.AddRange(alerts.Select(ToAlertRow));

                _connectionText = "실시간 연결됨 (SignalR)";
                if (_selectedDevice == null && _devices.Count > 0)
                {
                    var now = DateTimeOffset.Now;
                    _selectedDevice = (_devices.Values.FirstOrDefault(d => StatusOf(d, now) != "오프라인")
                                       ?? _devices.Values.First()).DeviceName;
                }

                _dirty = true;
                _alertsDirty = true;
            });

            await LoadHistoryAsync();
        }
        catch (Exception ex)
        {
            _connectionText = "초기 데이터 불러오기 재시도 중...";
            await Task.Delay(2000);
            _ = LoadSnapshotAsync();
        }
    }

    private async Task LoadHistoryAsync()
    {
        var name = _selectedDevice;
        if (name == null) return;

        try
        {
            var url = $"{BaseUrl}/api/devices/{Uri.EscapeDataString(name)}/history?limit={MaxPoints}";
            var history = await _http.GetFromJsonAsync<List<HistoryDto>>(url) ?? new();
            await Dispatcher.InvokeAsync(() =>
            {
                if (_selectedDevice == name)
                {
                    _history = history;
                    _dirty = true;
                }
            });
        }
        catch
        {
            // 다음 기회에 다시 불러옵니다
        }
    }

    private void OnMetric(MetricPush m)
    {
        if (!_devices.TryGetValue(m.DeviceName, out var state))
        {
            state = new DeviceState(m.DeviceName, m.Cpu, m.Memory, m.Timestamp);
            _devices[m.DeviceName] = state;
        }
        else if (m.Timestamp >= state.LastSeen)
        {
            state.Cpu = m.Cpu;
            state.Memory = m.Memory;
            state.LastSeen = m.Timestamp;
        }

        if (_selectedDevice == null) _selectedDevice = m.DeviceName;

        if (m.DeviceName == _selectedDevice)
        {
            _history.Add(new HistoryDto(m.Cpu, m.Memory, m.Timestamp.ToString("o")));
            while (_history.Count > MaxPoints) _history.RemoveAt(0);
        }

        _dirty = true;
    }

    private void OnAlert(AlertDto a)
    {
        _alerts.Insert(0, ToAlertRow(a));
        while (_alerts.Count > MaxAlerts) _alerts.RemoveAt(_alerts.Count - 1);
        _alertsDirty = true;
    }

    private static AlertRow ToAlertRow(AlertDto a) => new(
        a.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"),
        a.DeviceName,
        a.Type,
        a.Value,
        a.Threshold);

    private static string StatusOf(DeviceState d, DateTimeOffset now) =>
        (now - d.LastSeen).TotalSeconds > 10 ? "오프라인"
            : d.Cpu >= 80 ? "경고" : "정상";

    private void Render()
    {
        var now = DateTimeOffset.Now;
        var rows = _devices.Values
            .OrderBy(d => d.DeviceName)
            .Select(d => new DeviceRow(
                d.DeviceName, d.Cpu, d.Memory,
                d.LastSeen.ToLocalTime().ToString("HH:mm:ss"),
                StatusOf(d, now)))
            .ToList();

        _refreshing = true;
        DeviceGrid.ItemsSource = rows;
        DeviceGrid.SelectedItem = rows.FirstOrDefault(r => r.DeviceName == _selectedDevice);
        _refreshing = false;

        if (_alertsDirty)
        {
            AlertGrid.ItemsSource = _alerts.ToList();
            _alertsDirty = false;
        }

        StatusText.Text = $"{_connectionText} · 장비 {rows.Count}대 · 갱신 {DateTime.Now:HH:mm:ss}";
        DrawGraph();

        _dirty = false;
        _lastRender = DateTime.Now;
    }

    private async void DeviceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        if (DeviceGrid.SelectedItem is DeviceRow row)
        {
            _selectedDevice = row.DeviceName;
            _history = new();
            _dirty = true;
            await LoadHistoryAsync();
        }
    }

    private void GraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawGraph();

    private void DrawGraph()
    {
        GraphCanvas.Children.Clear();
        double w = GraphCanvas.ActualWidth;
        double h = GraphCanvas.ActualHeight;
        if (w < 60 || h < 40 || _selectedDevice == null) return;

        GraphTitle.Text = $"{_selectedDevice} · 최근 {_history.Count}초   (파랑: CPU, 주황: 메모리, 빨간 점선: 알람 기준 80%)";

        const double pad = 30;
        double plotW = w - pad - 10;
        double plotH = h - 20;
        double Y(double v) => 10 + plotH * (1 - v / 100.0);

        foreach (var v in new[] { 0, 20, 40, 60, 80, 100 })
        {
            var line = new Line
            {
                X1 = pad, X2 = pad + plotW, Y1 = Y(v), Y2 = Y(v),
                Stroke = v == 80 ? Brushes.IndianRed : Brushes.Gainsboro,
                StrokeThickness = 1
            };
            if (v == 80) line.StrokeDashArray = new DoubleCollection { 4, 3 };
            GraphCanvas.Children.Add(line);

            var label = new TextBlock { Text = v.ToString(), FontSize = 10, Foreground = Brushes.Gray };
            Canvas.SetLeft(label, 4);
            Canvas.SetTop(label, Y(v) - 7);
            GraphCanvas.Children.Add(label);
        }

        if (_history.Count < 2) return;

        var cpuPoints = new PointCollection();
        var memPoints = new PointCollection();
        int offset = MaxPoints - _history.Count;
        for (int i = 0; i < _history.Count; i++)
        {
            double x = pad + plotW * (i + offset) / (MaxPoints - 1);
            cpuPoints.Add(new Point(x, Y(_history[i].Cpu)));
            memPoints.Add(new Point(x, Y(_history[i].Memory)));
        }

        GraphCanvas.Children.Add(new Polyline { Points = memPoints, Stroke = Brushes.DarkOrange, StrokeThickness = 1.5 });
        GraphCanvas.Children.Add(new Polyline { Points = cpuPoints, Stroke = Brushes.SteelBlue, StrokeThickness = 2 });
    }
}

public class DeviceState
{
    public DeviceState(string deviceName, double cpu, double memory, DateTimeOffset lastSeen)
    {
        DeviceName = deviceName;
        Cpu = cpu;
        Memory = memory;
        LastSeen = lastSeen;
    }

    public string DeviceName { get; }
    public double Cpu { get; set; }
    public double Memory { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

public record MetricPush(string DeviceName, double Cpu, double Memory, DateTimeOffset Timestamp);
public record DeviceDto(string DeviceName, double Cpu, double Memory, DateTimeOffset LastSeen);
public record AlertDto(string DeviceName, string Type, double Value, double Threshold, DateTimeOffset TimestampUtc);
public record HistoryDto(double Cpu, double Memory, string Timestamp);
public record DeviceRow(string DeviceName, double Cpu, double Memory, string LastSeenLocal, string Status);
public record AlertRow(string Time, string DeviceName, string Type, double Value, double Threshold);



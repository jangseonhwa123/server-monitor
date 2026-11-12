using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Dashboard.Models;
using Dashboard.Services;

namespace Dashboard.ViewModels;

public class MainViewModel : ViewModelBase
{
    private const int MaxPoints = 60;
    private const int MaxAlerts = 20;

    private readonly MonitorClient _client;
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, DeviceRowViewModel> _byName = new();

    private DeviceRowViewModel? _selectedDevice;
    private string _statusText = "연결 중...";
    private bool _snapshotError;

    public ObservableCollection<DeviceRowViewModel> Devices { get; } = new();
    public ObservableCollection<AlertRow> Alerts { get; } = new();
    public List<HistoryDto> History { get; private set; } = new();

    public event Action? GraphInvalidated;

    public MainViewModel(MonitorClient client)
    {
        _client = client;
        _client.MetricReceived += m => _ui.Invoke(() => OnMetric(m));
        _client.AlertRaised += a => _ui.Invoke(() => OnAlert(a));
        _client.Connected += () => _ = LoadSnapshotAsync();
        _timer.Tick += (_, _) => Tick();
    }

    public DeviceRowViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetProperty(ref _selectedDevice, value)) return;
            History = new List<HistoryDto>();
            NotifyGraph();
            _ = LoadHistoryAsync();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string GraphTitle => SelectedDevice == null
        ? "장비를 선택하면 그래프가 표시됩니다"
        : $"{SelectedDevice.DeviceName} · 최근 {History.Count}초   (파랑: CPU, 주황: 메모리, 빨간 점선: 알람 기준 80%)";

    public async Task StartAsync()
    {
        _timer.Start();
        UpdateStatusText();
        await _client.StartAsync();
    }

    private void Tick()
    {
        var now = DateTimeOffset.Now;
        foreach (var row in Devices) row.RefreshStatus(now);
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        var connection = _client.State switch
        {
            ConnectionState.Connected => _snapshotError
                ? "실시간 연결됨 · 초기 데이터 재시도 중..."
                : "실시간 연결됨 (SignalR)",
            ConnectionState.Reconnecting => "재연결 중...",
            ConnectionState.Connecting => "연결 중...",
            _ => "서버 연결 끊김 · 재시도 중..."
        };
        StatusText = $"{connection} · 장비 {Devices.Count}대 · 갱신 {DateTime.Now:HH:mm:ss}";
    }

    // 처음 연결(또는 재연결) 때 현재 상태를 한 번 불러옵니다. 실패하면 2초 뒤 다시 시도합니다
    private async Task LoadSnapshotAsync()
    {
        while (_client.State == ConnectionState.Connected)
        {
            try
            {
                var devices = await _client.GetDevicesAsync();
                var alerts = await _client.GetAlertsAsync(MaxAlerts);
                _ui.Invoke(() => ApplySnapshot(devices, alerts));
                await LoadHistoryAsync();
                _snapshotError = false;
                return;
            }
            catch
            {
                _snapshotError = true;
                await Task.Delay(2000);
            }
        }
    }

    private void ApplySnapshot(List<DeviceDto> devices, List<AlertDto> alerts)
    {
        foreach (var d in devices)
        {
            Upsert(d.DeviceName).Update(d.Cpu, d.Memory, d.LastSeen);
        }

        var now = DateTimeOffset.Now;
        foreach (var row in Devices) row.RefreshStatus(now);

        Alerts.Clear();
        foreach (var a in alerts) Alerts.Add(ToAlertRow(a));

        if (SelectedDevice == null && Devices.Count > 0)
        {
            SelectedDevice = Devices.FirstOrDefault(r => r.Status != "오프라인") ?? Devices[0];
        }

        UpdateStatusText();
    }

    private async Task LoadHistoryAsync()
    {
        var target = SelectedDevice;
        if (target == null) return;

        try
        {
            var history = await _client.GetHistoryAsync(target.DeviceName, MaxPoints);
            _ui.Invoke(() =>
            {
                if (SelectedDevice == target)
                {
                    History = history;
                    NotifyGraph();
                }
            });
        }
        catch
        {
            // 다음 연결 때 다시 불러옵니다
        }
    }

    private DeviceRowViewModel Upsert(string name)
    {
        if (_byName.TryGetValue(name, out var row)) return row;

        row = new DeviceRowViewModel(name);
        _byName[name] = row;

        var index = 0;
        while (index < Devices.Count && string.CompareOrdinal(Devices[index].DeviceName, name) < 0) index++;
        Devices.Insert(index, row);
        return row;
    }

    private void OnMetric(MetricPush m)
    {
        var row = Upsert(m.DeviceName);
        row.Update(m.Cpu, m.Memory, m.Timestamp);
        row.RefreshStatus(DateTimeOffset.Now);

        if (SelectedDevice == null) SelectedDevice = row;

        if (SelectedDevice == row)
        {
            History.Add(new HistoryDto(m.Cpu, m.Memory, m.Timestamp.ToString("o")));
            while (History.Count > MaxPoints) History.RemoveAt(0);
            NotifyGraph();
        }
    }

    private void OnAlert(AlertDto a)
    {
        Alerts.Insert(0, ToAlertRow(a));
        while (Alerts.Count > MaxAlerts) Alerts.RemoveAt(Alerts.Count - 1);
    }

    private static AlertRow ToAlertRow(AlertDto a) => new(
        a.TimestampUtc.ToLocalTime().ToString("MM-dd HH:mm:ss"),
        a.DeviceName,
        a.Type,
        a.Value,
        a.Threshold);

    private void NotifyGraph()
    {
        OnPropertyChanged(nameof(GraphTitle));
        GraphInvalidated?.Invoke();
    }
}

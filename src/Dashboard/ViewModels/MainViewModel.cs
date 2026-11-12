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

    public ObservableCollection<TopItem> TopCpu { get; } = new();
    public ObservableCollection<TopItem> TopMemory { get; } = new();

    private int _okCount, _warnCount, _offlineCount;
    private string _alertCountText = "0", _latestAlertText = "발생한 알람이 없습니다", _alertTopText = "";
    private string _selectedTitle = "선택 장비", _currentCpuText = "-", _currentMemoryText = "-", _avgCpuText = "-", _maxCpuText = "-";
    private bool _isConnected;

    public int OkCount { get => _okCount; private set => SetProperty(ref _okCount, value); }
    public int WarnCount { get => _warnCount; private set => SetProperty(ref _warnCount, value); }
    public int OfflineCount { get => _offlineCount; private set => SetProperty(ref _offlineCount, value); }
    public string AlertCountText { get => _alertCountText; private set => SetProperty(ref _alertCountText, value); }
    public string LatestAlertText { get => _latestAlertText; private set => SetProperty(ref _latestAlertText, value); }
    public string AlertTopText { get => _alertTopText; private set => SetProperty(ref _alertTopText, value); }
    public string SelectedTitle { get => _selectedTitle; private set => SetProperty(ref _selectedTitle, value); }
    public string CurrentCpuText { get => _currentCpuText; private set => SetProperty(ref _currentCpuText, value); }
    public string CurrentMemoryText { get => _currentMemoryText; private set => SetProperty(ref _currentMemoryText, value); }
    public string AvgCpuText { get => _avgCpuText; private set => SetProperty(ref _avgCpuText, value); }
    public string MaxCpuText { get => _maxCpuText; private set => SetProperty(ref _maxCpuText, value); }
    public bool IsConnected { get => _isConnected; private set => SetProperty(ref _isConnected, value); }

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
        RefreshSummary();
    }

    private void UpdateStatusText()
    {
        IsConnected = _client.State == ConnectionState.Connected;
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
        RefreshSummary();
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
        RefreshSummary();
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
        UpdateKpis();
        GraphInvalidated?.Invoke();
    }

    // 상단 위젯(상태 개수, Top 5, 알람 요약)과 선택 장비 KPI를 다시 계산합니다
    private void RefreshSummary()
    {
        int ok = 0, warn = 0, off = 0;
        foreach (var d in Devices)
        {
            if (d.Status == "정상") ok++;
            else if (d.Status == "경고") warn++;
            else off++;
        }
        OkCount = ok;
        WarnCount = warn;
        OfflineCount = off;

        var online = Devices.Where(d => d.Status != "오프라인").ToList();
        Replace(TopCpu, online.OrderByDescending(d => d.Cpu).Take(5).Select(d => new TopItem(d.DeviceName, d.Cpu, d.Cpu >= 80)));
        Replace(TopMemory, online.OrderByDescending(d => d.Memory).Take(5).Select(d => new TopItem(d.DeviceName, d.Memory, false)));

        AlertCountText = Alerts.Count.ToString();
        LatestAlertText = Alerts.Count > 0
            ? $"가장 최근: {Alerts[0].DeviceName} · {Alerts[0].Time}"
            : "발생한 알람이 없습니다";
        AlertTopText = string.Join(Environment.NewLine,
            Alerts.GroupBy(a => a.DeviceName)
                  .OrderByDescending(g => g.Count())
                  .Take(2)
                  .Select(g => $"{g.Key} · {g.Count()}건"));

        UpdateKpis();
        GraphInvalidated?.Invoke();
    }

    private void UpdateKpis()
    {
        var device = SelectedDevice;
        SelectedTitle = device == null ? "선택 장비" : $"선택 장비 · {device.DeviceName}";
        CurrentCpuText = device == null ? "-" : $"{device.Cpu:F1}%";
        CurrentMemoryText = device == null ? "-" : $"{device.Memory:F1}%";
        AvgCpuText = History.Count > 0 ? $"{History.Average(h => h.Cpu):F1}%" : "-";
        MaxCpuText = History.Count > 0 ? $"{History.Max(h => h.Cpu):F1}%" : "-";
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var list = items.ToList();
        target.Clear();
        foreach (var item in list) target.Add(item);
    }
}

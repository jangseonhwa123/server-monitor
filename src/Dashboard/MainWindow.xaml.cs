using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Dashboard;

public partial class MainWindow : Window
{
    private const string BaseUrl = "http://localhost:5000";
    private const int MaxPoints = 60;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private string? _selectedDevice;
    private bool _refreshing;
    private bool _busy;
    private List<HistoryDto> _history = new();

    public MainWindow()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var devices = await _http.GetFromJsonAsync<List<DeviceDto>>($"{BaseUrl}/api/devices") ?? new();
            var alerts = await _http.GetFromJsonAsync<List<AlertDto>>($"{BaseUrl}/api/alerts?limit=20") ?? new();

            var rows = devices.Select(d => new DeviceRow(
                d.DeviceName,
                d.Cpu,
                d.Memory,
                d.LastSeen.ToLocalTime().ToString("HH:mm:ss"),
                (DateTimeOffset.Now - d.LastSeen).TotalSeconds > 10 ? "오프라인"
                    : d.Cpu >= 80 ? "경고" : "정상")).ToList();

            if (_selectedDevice == null && rows.Count > 0)
            {
                _selectedDevice = (rows.FirstOrDefault(r => r.Status != "오프라인") ?? rows[0]).DeviceName;
            }

            _refreshing = true;
            DeviceGrid.ItemsSource = rows;
            DeviceGrid.SelectedItem = rows.FirstOrDefault(r => r.DeviceName == _selectedDevice);
            _refreshing = false;

            AlertGrid.ItemsSource = alerts.Select(a => new AlertRow(
                a.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"),
                a.DeviceName,
                a.Type,
                a.Value,
                a.Threshold)).ToList();

            if (_selectedDevice != null)
            {
                var url = $"{BaseUrl}/api/devices/{Uri.EscapeDataString(_selectedDevice)}/history?limit={MaxPoints}";
                _history = await _http.GetFromJsonAsync<List<HistoryDto>>(url) ?? new();
                DrawGraph();
            }

            StatusText.Text = $"서버 연결됨 · 장비 {devices.Count}대 · 갱신 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _refreshing = false;
            StatusText.Text = $"서버 연결 실패: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    private async void DeviceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        if (DeviceGrid.SelectedItem is DeviceRow row)
        {
            _selectedDevice = row.DeviceName;
            await RefreshAsync();
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

public record DeviceDto(string DeviceName, double Cpu, double Memory, DateTimeOffset LastSeen);
public record AlertDto(string DeviceName, string Type, double Value, double Threshold, DateTimeOffset TimestampUtc);
public record HistoryDto(double Cpu, double Memory, string Timestamp);
public record DeviceRow(string DeviceName, double Cpu, double Memory, string LastSeenLocal, string Status);
public record AlertRow(string Time, string DeviceName, string Type, double Value, double Threshold);

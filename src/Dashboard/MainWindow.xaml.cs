using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Dashboard.Models;
using Dashboard.Services;
using Dashboard.ViewModels;

namespace Dashboard;

// 화면 모양과 그래프/도넛 그리기만 담당합니다 (데이터와 로직은 MainViewModel)
public partial class MainWindow : Window
{
    private const int MaxPoints = 60;

    private static readonly SolidColorBrush GridBrush = Frozen("#E6E6E6");
    private static readonly SolidColorBrush SubBrush = Frozen("#525252");
    private static readonly SolidColorBrush InkBrush = Frozen("#000000");
    private static readonly SolidColorBrush CpuBrush = Frozen("#E896D9");
    private static readonly SolidColorBrush MemBrush = Frozen("#FEDBBE");
    private static readonly SolidColorBrush OkBrush = Frozen("#99D586");
    private static readonly SolidColorBrush WarnBrush = Frozen("#F29797");
    private static readonly SolidColorBrush OffBrush = Frozen("#A5A5A5");
    private static readonly SolidColorBrush BadBrush = Frozen("#FF7066");

    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();

        var vm = new MainViewModel(new MonitorClient("http://127.0.0.1:5000"));
        vm.GraphInvalidated += DrawAll;
        _vm = vm;
        DataContext = vm;

        Loaded += async (_, _) => await vm.StartAsync();
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawAll();

    private void DrawAll()
    {
        DrawDonut();
        DrawGraph(CpuCanvas, h => h.Cpu, CpuBrush, drawThreshold: true);
        DrawGraph(MemCanvas, h => h.Memory, MemBrush, drawThreshold: false);
    }

    private static void AddText(Canvas canvas, string text, double x, double y, double width,
        TextAlignment align, double size = 11, Brush? brush = null, bool bold = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = brush ?? SubBrush,
            Width = width,
            TextAlignment = align,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        canvas.Children.Add(tb);
    }

    // 장비 상태(정상/경고/오프라인) 비율 도넛
    private void DrawDonut()
    {
        DonutCanvas.Children.Clear();
        var vm = _vm;
        if (vm == null) return;

        const double size = 110, thickness = 16;
        const double center = size / 2;
        const double radius = (size - thickness) / 2 - 2;

        int total = vm.OkCount + vm.WarnCount + vm.OfflineCount;
        Point PointAt(double angle) => new(center + radius * Math.Cos(angle), center + radius * Math.Sin(angle));

        void AddRing(Brush brush)
        {
            var ring = new Ellipse { Width = radius * 2, Height = radius * 2, Stroke = brush, StrokeThickness = thickness };
            Canvas.SetLeft(ring, center - radius);
            Canvas.SetTop(ring, center - radius);
            DonutCanvas.Children.Add(ring);
        }

        if (total == 0)
        {
            AddRing(GridBrush);
        }
        else
        {
            var segments = new (int Count, Brush Color)[]
            {
                (vm.OkCount, OkBrush), (vm.WarnCount, WarnBrush), (vm.OfflineCount, OffBrush)
            };

            double done = 0;
            foreach (var (count, color) in segments)
            {
                if (count == 0) continue;
                if (count == total) { AddRing(color); break; }

                double start = -Math.PI / 2 + 2 * Math.PI * done / total;
                double end = start + 2 * Math.PI * count / total;
                var figure = new PathFigure { StartPoint = PointAt(start), IsClosed = false };
                figure.Segments.Add(new ArcSegment(PointAt(end), new Size(radius, radius), 0,
                    end - start > Math.PI, SweepDirection.Clockwise, true));
                DonutCanvas.Children.Add(new System.Windows.Shapes.Path
                {
                    Stroke = color,
                    StrokeThickness = thickness,
                    Data = new PathGeometry(new[] { figure })
                });
                done += count;
            }
        }

        AddText(DonutCanvas, total.ToString(), 0, 36, size, TextAlignment.Center, 24, InkBrush, bold: true);
        AddText(DonutCanvas, "전체 장비", 0, 66, size, TextAlignment.Center, 10);
    }

    // 선택한 장비의 최근 60초 그래프 (선 + 채우기)
    private void DrawGraph(Canvas canvas, Func<HistoryDto, double> pick, SolidColorBrush color, bool drawThreshold)
    {
        canvas.Children.Clear();
        double w = canvas.ActualWidth;
        double h = canvas.ActualHeight;
        if (w < 80 || h < 60) return;

        const double left = 34, right = 10, top = 10, bottom = 22;
        double plotW = w - left - right;
        double plotH = h - top - bottom;
        double Y(double v) => top + plotH * (1 - Math.Clamp(v, 0, 100) / 100.0);

        foreach (var v in new[] { 0, 25, 50, 75, 100 })
        {
            canvas.Children.Add(new Line
            {
                X1 = left, X2 = left + plotW, Y1 = Y(v), Y2 = Y(v),
                Stroke = GridBrush, StrokeThickness = 1
            });
            AddText(canvas, v.ToString(), 0, Y(v) - 8, left - 6, TextAlignment.Right);
        }

        if (drawThreshold)
        {
            canvas.Children.Add(new Line
            {
                X1 = left, X2 = left + plotW, Y1 = Y(80), Y2 = Y(80),
                Stroke = BadBrush, StrokeThickness = 1.2,
                StrokeDashArray = new DoubleCollection { 5, 4 }
            });
        }

        var history = _vm?.History;
        if (_vm?.SelectedDevice == null || history == null || history.Count == 0)
        {
            AddText(canvas, "표시할 데이터가 없습니다", 0, h / 2 - 8, w, TextAlignment.Center, 12);
            return;
        }

        int offset = MaxPoints - history.Count;
        double X(int i) => left + plotW * (i + offset) / (MaxPoints - 1);

        var points = new PointCollection();
        for (int i = 0; i < history.Count; i++) points.Add(new Point(X(i), Y(pick(history[i]))));

        var area = new PointCollection { new Point(X(0), Y(0)) };
        foreach (var p in points) area.Add(p);
        area.Add(new Point(X(history.Count - 1), Y(0)));

        canvas.Children.Add(new Polygon { Points = area, Fill = color, Opacity = 0.16 });
        canvas.Children.Add(new Polyline
        {
            Points = points, Stroke = color, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round
        });

        var last = points[points.Count - 1];
        var dot = new Ellipse { Width = 8, Height = 8, Fill = color };
        Canvas.SetLeft(dot, last.X - 4);
        Canvas.SetTop(dot, last.Y - 4);
        canvas.Children.Add(dot);

        AddText(canvas, "60초 전", left, h - 18, 80, TextAlignment.Left);
        AddText(canvas, $"현재 {pick(history[history.Count - 1]):F1}%", w - right - 140, h - 18, 140, TextAlignment.Right);
    }
}

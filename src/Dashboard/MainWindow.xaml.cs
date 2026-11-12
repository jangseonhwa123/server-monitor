using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Dashboard.Services;
using Dashboard.ViewModels;

namespace Dashboard;

// 화면 모양과 그래프 그리기만 담당합니다 (데이터와 로직은 MainViewModel)
public partial class MainWindow : Window
{
    private const int MaxPoints = 60;
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();

        var vm = new MainViewModel(new MonitorClient("http://127.0.0.1:5000"));
        vm.GraphInvalidated += DrawGraph;
        _vm = vm;
        DataContext = vm;

        Loaded += async (_, _) => await vm.StartAsync();
    }

    private void GraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawGraph();

    private void DrawGraph()
    {
        var vm = _vm;
        GraphCanvas.Children.Clear();
        double w = GraphCanvas.ActualWidth;
        double h = GraphCanvas.ActualHeight;
        if (vm == null || w < 60 || h < 40 || vm.SelectedDevice == null) return;

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

        var history = vm.History;
        if (history.Count < 2) return;

        var cpuPoints = new PointCollection();
        var memPoints = new PointCollection();
        int offset = MaxPoints - history.Count;
        for (int i = 0; i < history.Count; i++)
        {
            double x = pad + plotW * (i + offset) / (MaxPoints - 1);
            cpuPoints.Add(new Point(x, Y(history[i].Cpu)));
            memPoints.Add(new Point(x, Y(history[i].Memory)));
        }

        GraphCanvas.Children.Add(new Polyline { Points = memPoints, Stroke = Brushes.DarkOrange, StrokeThickness = 1.5 });
        GraphCanvas.Children.Add(new Polyline { Points = cpuPoints, Stroke = Brushes.SteelBlue, StrokeThickness = 2 });
    }
}


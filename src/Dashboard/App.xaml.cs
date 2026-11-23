using System.Configuration;
using System.Data;
using System.Windows;

namespace Dashboard;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        // 화면이 멈추거나 까맣게 될 때 원인을 알 수 있도록, 처리되지 않은 오류를 창으로 보여줍니다
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.ToString(), "PulseMon 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }
}


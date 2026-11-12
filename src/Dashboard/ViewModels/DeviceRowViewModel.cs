namespace Dashboard.ViewModels;

// 장비 목록의 한 줄. 값이 바뀌면 화면이 자동으로 갱신됩니다
public class DeviceRowViewModel : ViewModelBase
{
    private double _cpu;
    private double _memory;
    private string _lastSeenLocal = "-";
    private string _status = "오프라인";

    public DeviceRowViewModel(string deviceName)
    {
        DeviceName = deviceName;
    }

    public string DeviceName { get; }
    public DateTimeOffset LastSeen { get; private set; } = DateTimeOffset.MinValue;

    public double Cpu { get => _cpu; private set => SetProperty(ref _cpu, value); }
    public double Memory { get => _memory; private set => SetProperty(ref _memory, value); }
    public string LastSeenLocal { get => _lastSeenLocal; private set => SetProperty(ref _lastSeenLocal, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public void Update(double cpu, double memory, DateTimeOffset seen)
    {
        if (seen < LastSeen) return; // 재전송된 오래된 값은 최신 상태를 덮어쓰지 않음
        Cpu = cpu;
        Memory = memory;
        LastSeen = seen;
        LastSeenLocal = seen.ToLocalTime().ToString("HH:mm:ss");
    }

    public void RefreshStatus(DateTimeOffset now)
    {
        Status = (now - LastSeen).TotalSeconds > 10 ? "오프라인"
            : Cpu >= 80 ? "경고" : "정상";
    }
}

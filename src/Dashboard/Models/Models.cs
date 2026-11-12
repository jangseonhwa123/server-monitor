namespace Dashboard.Models;

public record MetricPush(string DeviceName, double Cpu, double Memory, DateTimeOffset Timestamp);
public record DeviceDto(string DeviceName, double Cpu, double Memory, DateTimeOffset LastSeen);
public record AlertDto(string DeviceName, string Type, double Value, double Threshold, DateTimeOffset TimestampUtc);
public record HistoryDto(double Cpu, double Memory, string Timestamp);
public record AlertRow(string Time, string DeviceName, string Type, double Value, double Threshold)
{
    public string ValueText => $"{Value:F1}% / {Threshold:F0}%";
}

public record TopItem(string Name, double Value, bool IsHigh);

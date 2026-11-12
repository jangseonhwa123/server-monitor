// 지표 입력값 검증. 문제가 있으면 이유를, 없으면 null을 돌려줍니다
internal static class MetricValidator
{
    private const int MaxDeviceNameLength = 64;
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);

    public static string? Validate(MetricDto metric)
    {
        if (string.IsNullOrWhiteSpace(metric.DeviceName))
            return "deviceName is required";

        if (metric.DeviceName.Length > MaxDeviceNameLength)
            return $"deviceName must be at most {MaxDeviceNameLength} characters";

        if (!IsPercent(metric.Cpu))
            return "cpu must be between 0 and 100";

        if (!IsPercent(metric.Memory))
            return "memory must be between 0 and 100";

        if (metric.Timestamp.Year < 2000)
            return "timestamp is required";

        if (metric.Timestamp.ToUniversalTime() > DateTime.UtcNow + MaxFutureSkew)
            return "timestamp is too far in the future";

        return null;
    }

    private static bool IsPercent(double value) => double.IsFinite(value) && value >= 0 && value <= 100;
}

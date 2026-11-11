using System.Diagnostics;
using System.Net.Http.Json;

// 사용법: Agent [장비이름] [sim]
var deviceName = args.Length > 0 ? args[0] : Environment.MachineName;
var simulate = args.Length > 1 && args[1] == "sim";
var serverUrl = "http://localhost:5000/api/metrics";

var cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total");
var mem = new PerformanceCounter("Memory", "% Committed Bytes In Use");
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

cpu.NextValue(); // 첫 호출은 항상 0이라 버립니다
Console.WriteLine($"장비 이름: {deviceName} / 모드: {(simulate ? "시뮬레이션" : "실제 측정")}");

while (true)
{
    await Task.Delay(1000);

    double cpuValue = simulate ? Random.Shared.NextDouble() * 100 : cpu.NextValue();
    double memValue = simulate ? 40 + Random.Shared.NextDouble() * 50 : mem.NextValue();

    var metric = new MetricDto(
        deviceName,
        Math.Round(cpuValue, 1),
        Math.Round(memValue, 1),
        DateTime.Now);

    try
    {
        var response = await http.PostAsJsonAsync(serverUrl, metric);
        var status = response.IsSuccessStatusCode
            ? "성공"
            : "실패(" + (int)response.StatusCode + ")";
        Console.WriteLine($"{metric.Timestamp:HH:mm:ss}  {deviceName}  CPU {metric.Cpu:F1}%  메모리 {metric.Memory:F1}%  -> 전송 {status}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss}  서버 연결 실패: {ex.Message}");
    }
}

record MetricDto(string DeviceName, double Cpu, double Memory, DateTime Timestamp);

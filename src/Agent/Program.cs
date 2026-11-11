using System.Diagnostics;
using System.Net.Http.Json;

var serverUrl = "http://localhost:5000/api/metrics";
var deviceName = Environment.MachineName;

var cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total");
var mem = new PerformanceCounter("Memory", "% Committed Bytes In Use");
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

cpu.NextValue(); // 첫 호출은 항상 0이라 버립니다

while (true)
{
    await Task.Delay(1000);

    var metric = new MetricDto(
        deviceName,
        Math.Round(cpu.NextValue(), 1),
        Math.Round(mem.NextValue(), 1),
        DateTime.Now);

    try
    {
        var response = await http.PostAsJsonAsync(serverUrl, metric);
        var status = response.IsSuccessStatusCode
            ? "성공"
            : "실패(" + (int)response.StatusCode + ")";
        Console.WriteLine($"{metric.Timestamp:HH:mm:ss}  CPU {metric.Cpu:F1}%  메모리 {metric.Memory:F1}%  -> 전송 {status}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss}  서버 연결 실패: {ex.Message}");
    }
}

record MetricDto(string DeviceName, double Cpu, double Memory, DateTime Timestamp);

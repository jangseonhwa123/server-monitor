using System.Diagnostics;
using System.Net.Http.Json;

// 사용법: Agent [장비이름] [sim]
var deviceName = args.Length > 0 ? args[0] : Environment.MachineName;
var simulate = args.Length > 1 && args[1] == "sim";
var serverUrl = "http://localhost:5000/api/metrics";

const int MaxPending = 3600;    // 서버가 꺼져 있을 때 최대 1시간치 보관
const int MaxSendPerTick = 50;  // 복구 시 한 번에 재전송할 최대 개수

var cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total");
var mem = new PerformanceCounter("Memory", "% Committed Bytes In Use");
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

var pending = new Queue<MetricDto>();
double simCpu = 40, simMem = 60;

cpu.NextValue(); // 첫 호출은 항상 0이라 버립니다
Console.WriteLine($"장비 이름: {deviceName} / 모드: {(simulate ? "시뮬레이션" : "실제 측정")}");

while (true)
{
    await Task.Delay(1000);

    double cpuValue, memValue;
    if (simulate)
    {
        // 값이 서서히 변하도록 이전 값에서 조금씩 움직입니다
        simCpu = Math.Clamp(simCpu + (Random.Shared.NextDouble() - 0.5) * 18, 5, 100);
        simMem = Math.Clamp(simMem + (Random.Shared.NextDouble() - 0.5) * 3, 40, 90);
        cpuValue = simCpu;
        memValue = simMem;
    }
    else
    {
        cpuValue = cpu.NextValue();
        memValue = mem.NextValue();
    }

    var latest = new MetricDto(deviceName, Math.Round(cpuValue, 1), Math.Round(memValue, 1), DateTime.Now);
    pending.Enqueue(latest);
    while (pending.Count > MaxPending) pending.Dequeue();

    var sent = 0;
    try
    {
        // 오래된 것부터 순서대로 전송합니다 (서버가 꺼져 있었다면 쌓인 것까지 한꺼번에)
        while (pending.Count > 0 && sent < MaxSendPerTick)
        {
            var response = await http.PostAsJsonAsync(serverUrl, pending.Peek());
            var code = (int)response.StatusCode;
            if (response.IsSuccessStatusCode || (code >= 400 && code < 500))
            {
                pending.Dequeue();
                sent++;
            }
            else
            {
                throw new HttpRequestException("HTTP " + code);
            }
        }

        Console.WriteLine($"{latest.Timestamp:HH:mm:ss}  {deviceName}  CPU {latest.Cpu:F1}%  메모리 {latest.Memory:F1}%  -> 전송 {sent}건 (대기 {pending.Count}건)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {deviceName}  서버 연결 실패: {ex.Message} · 대기 {pending.Count}건 보관 중");
    }
}

record MetricDto(string DeviceName, double Cpu, double Memory, DateTime Timestamp);

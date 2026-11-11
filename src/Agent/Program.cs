using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;

// 사용법: Agent [장비이름] [sim]
// API 키는 환경 변수 MONITOR_API_KEY 로 지정합니다 (없으면 개발용 기본 키 사용)
var deviceName = args.Length > 0 ? args[0] : Environment.MachineName;
var simulate = args.Length > 1 && args[1] == "sim";
var serverUrl = "http://localhost:5000/api/metrics";
var apiKey = Environment.GetEnvironmentVariable("MONITOR_API_KEY") ?? "dev-secret-key";

const int MaxPending = 3600; // 서버가 꺼져 있을 때 최대 1시간치 보관

var cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total");
var mem = new PerformanceCounter("Memory", "% Committed Bytes In Use");

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

var queue = new ConcurrentQueue<MetricDto>();
long sentTotal = 0;
bool wasFailing = false;

// 전송 흐름: 대기열의 오래된 것부터 서버로 보냅니다
_ = Task.Run(async () =>
{
    while (true)
    {
        if (!queue.TryPeek(out var item))
        {
            await Task.Delay(200);
            continue;
        }

        try
        {
            var response = await http.PostAsJsonAsync(serverUrl, item);
            var code = (int)response.StatusCode;

            if (response.IsSuccessStatusCode || (code >= 400 && code < 500 && code != 401))
            {
                queue.TryDequeue(out _);
                Interlocked.Increment(ref sentTotal);
                if (wasFailing)
                {
                    wasFailing = false;
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss}  서버 연결 복구 · 대기 {queue.Count}건 재전송 중");
                }
                continue;
            }

            throw new HttpRequestException(code == 401 ? "인증 실패(API 키 확인)" : "HTTP " + code);
        }
        catch (Exception ex)
        {
            wasFailing = true;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss}  전송 실패: {ex.Message} · 대기 {queue.Count}건 보관 중");
            await Task.Delay(2000); // 재시도 간격
        }
    }
});

// 수집 흐름: 서버 상태와 상관없이 1초마다 측정합니다
double simCpu = 40, simMem = 60;
cpu.NextValue(); // 첫 호출은 항상 0이라 버립니다
Console.WriteLine($"장비 이름: {deviceName} / 모드: {(simulate ? "시뮬레이션" : "실제 측정")}");

while (true)
{
    await Task.Delay(1000);

    double cpuValue, memValue;
    if (simulate)
    {
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
    queue.Enqueue(latest);
    while (queue.Count > MaxPending) queue.TryDequeue(out _);

    Console.WriteLine($"{latest.Timestamp:HH:mm:ss}  {deviceName}  CPU {latest.Cpu:F1}%  메모리 {latest.Memory:F1}%  (전송 누계 {Interlocked.Read(ref sentTotal)}건, 대기 {queue.Count}건)");
}

record MetricDto(string DeviceName, double Cpu, double Memory, DateTime Timestamp);

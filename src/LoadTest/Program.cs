using System.Diagnostics;
using System.Net.Http.Json;

// 사용법: dotnet run -c Release --project src/LoadTest -- --agents 100 --duration 20
var agents = GetInt("--agents", 10);
var seconds = GetInt("--duration", 20);
var intervalMs = GetInt("--interval", 1000);
var baseUrl = GetStr("--url", "http://127.0.0.1:5000");
var apiKey = Environment.GetEnvironmentVariable("MONITOR_API_KEY") ?? "dev-secret-key";

var handler = new SocketsHttpHandler { MaxConnectionsPerServer = Math.Max(agents, 10) };
using var http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

// 첫 요청은 연결 수립과 JIT 때문에 느리므로 측정에서 제외합니다
await Send("warmup");

Console.WriteLine($"부하 테스트 시작: 가상 에이전트 {agents}대, 간격 {intervalMs}ms, {seconds}초");
Console.WriteLine($"목표 처리량: 초당 {agents * 1000.0 / intervalMs:F0}건");

var total = Stopwatch.StartNew();
var tasks = Enumerable.Range(1, agents).Select(Worker).ToArray();
var results = await Task.WhenAll(tasks);
total.Stop();

var latencies = results.SelectMany(r => r.Latencies).OrderBy(x => x).ToList();
var ok = results.Sum(r => r.Ok);
var fail = results.Sum(r => r.Fail);
var elapsed = total.Elapsed.TotalSeconds;

Console.WriteLine();
Console.WriteLine($"성공 {ok}건, 실패 {fail}건, 실제 처리량 {ok / elapsed:F0}건/초");
if (latencies.Count > 0)
{
    Console.WriteLine($"응답 시간(ms): 평균 {latencies.Average():F1}, p50 {Pct(50):F1}, p95 {Pct(95):F1}, p99 {Pct(99):F1}, 최대 {latencies.Max():F1}");
    Console.WriteLine($"표 한 줄: | {agents} | {ok / elapsed:F0} | {latencies.Average():F1} | {Pct(95):F1} | {Pct(99):F1} | {fail} |");
}

double Pct(double p) => latencies[Math.Max(0, (int)Math.Ceiling(p / 100.0 * latencies.Count) - 1)];

async Task<(List<double> Latencies, int Ok, int Fail)> Worker(int id)
{
    var rnd = new Random(id);
    var lat = new List<double>();
    int okCount = 0, failCount = 0;
    var name = $"load-{id:D4}";
    var clock = Stopwatch.StartNew();
    long nextAt = rnd.Next(intervalMs); // 시작 시점을 흩뿌려서 한꺼번에 몰리지 않게 함

    while (nextAt < seconds * 1000L)
    {
        var wait = nextAt - clock.ElapsedMilliseconds;
        if (wait > 0) await Task.Delay((int)wait);

        var t0 = Stopwatch.GetTimestamp();
        bool success = await Send(name, rnd.Next(5, 70), rnd.Next(40, 90));
        var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        if (success) { okCount++; lat.Add(ms); } else { failCount++; }
        nextAt += intervalMs;
    }
    return (lat, okCount, failCount);
}

async Task<bool> Send(string name, double cpu = 10, double mem = 50)
{
    try
    {
        var resp = await http.PostAsJsonAsync("/api/metrics",
            new { DeviceName = name, Cpu = cpu, Memory = mem, Timestamp = DateTime.UtcNow });
        return resp.IsSuccessStatusCode;
    }
    catch
    {
        return false;
    }
}

string GetStr(string name, string def)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
}

int GetInt(string name, int def) => int.TryParse(GetStr(name, ""), out var v) ? v : def;

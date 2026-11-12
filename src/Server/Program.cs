using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;

const double CpuThreshold = 80.0;
const int AlertCooldownSeconds = 60;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.AddSignalR();
var app = builder.Build();

var dbPath = builder.Configuration["MONITOR_DB_PATH"] ?? Path.Combine(builder.Environment.ContentRootPath, "monitor.db");
var connectionString = $"Data Source={dbPath}";

SqliteConnection OpenDb()
{
    var conn = new SqliteConnection(connectionString);
    conn.Open();
    return conn;
}

using (var conn = OpenDb())
{
    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS Metrics (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            DeviceName TEXT NOT NULL,
            Cpu REAL NOT NULL,
            Memory REAL NOT NULL,
            Timestamp TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS Alerts (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            DeviceName TEXT NOT NULL,
            Type TEXT NOT NULL,
            Value REAL NOT NULL,
            Threshold REAL NOT NULL,
            Timestamp TEXT NOT NULL
        );
        """;
    cmd.ExecuteNonQuery();
}

// 오래된 데이터 자동 삭제 (보존 기간은 MONITOR_RETENTION_DAYS, 기본 7일)
var retentionDays = int.TryParse(app.Configuration["MONITOR_RETENTION_DAYS"], out var parsedDays) && parsedDays > 0 ? parsedDays : 7;
_ = Task.Run(() => RetentionJob.RunAsync(OpenDb, TimeSpan.FromDays(retentionDays), TimeSpan.FromHours(1), app.Lifetime.ApplicationStopping));

// 에이전트 인증: POST /api/metrics 는 올바른 API 키가 있어야 합니다
var apiKey = app.Configuration["MONITOR_API_KEY"] ?? "dev-secret-key";

app.Use(async (context, next) =>
{
    if (context.Request.Method == "POST" && context.Request.Path.StartsWithSegments("/api/metrics"))
    {
        if (!context.Request.Headers.TryGetValue("X-Api-Key", out var key) || key != apiKey)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Invalid API key");
            return;
        }
    }
    await next();
});

// 실시간 푸시용 SignalR 허브
app.MapHub<MonitorHub>("/hub/monitor");

app.MapGet("/", () => "Server Monitor is running");

app.MapPost("/api/metrics", async (MetricDto metric, IHubContext<MonitorHub> hub) =>
{
    var validationError = MetricValidator.Validate(metric);
    if (validationError != null)
    {
        return Results.BadRequest(new { error = validationError });
    }

    if (!MetricQueue.Enqueue(metric, OpenDb))
    {
        return Results.StatusCode(503);
    }

    // 연결된 모든 관제 화면에 즉시 전달
    await hub.Clients.All.SendAsync("MetricReceived", metric);

    // CPU 임계값 초과 시 알람 기록 (같은 장비는 60초에 한 번만)
    if (metric.Cpu >= CpuThreshold)
    {
        using var conn = OpenDb();
        var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM Alerts WHERE DeviceName = $d AND Type = 'CPU' AND Timestamp > $since";
        check.Parameters.AddWithValue("$d", metric.DeviceName);
        check.Parameters.AddWithValue("$since", DateTime.UtcNow.AddSeconds(-AlertCooldownSeconds).ToString("o"));
        var recent = (long)check.ExecuteScalar()!;

        if (recent == 0)
        {
            var alertTime = DateTime.UtcNow;
            var alert = conn.CreateCommand();
            alert.CommandText = "INSERT INTO Alerts (DeviceName, Type, Value, Threshold, Timestamp) VALUES ($d, 'CPU', $v, $th, $ts)";
            alert.Parameters.AddWithValue("$d", metric.DeviceName);
            alert.Parameters.AddWithValue("$v", metric.Cpu);
            alert.Parameters.AddWithValue("$th", CpuThreshold);
            alert.Parameters.AddWithValue("$ts", alertTime.ToString("o"));
            alert.ExecuteNonQuery();
            Console.WriteLine($"[ALERT] {metric.DeviceName} CPU {metric.Cpu:F1}% (기준 {CpuThreshold}%)");

            await hub.Clients.All.SendAsync("AlertRaised", new
            {
                DeviceName = metric.DeviceName,
                Type = "CPU",
                Value = metric.Cpu,
                Threshold = CpuThreshold,
                TimestampUtc = alertTime
            });
        }
    }

    return Results.Ok(new { received = true });
});

app.MapGet("/api/metrics", (int? limit) =>
{
    using var conn = OpenDb();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT DeviceName, Cpu, Memory, Timestamp FROM Metrics ORDER BY Id DESC LIMIT $l";
    cmd.Parameters.AddWithValue("$l", limit ?? 10);
    using var reader = cmd.ExecuteReader();
    var list = new List<object>();
    while (reader.Read())
    {
        list.Add(new
        {
            DeviceName = reader.GetString(0),
            Cpu = reader.GetDouble(1),
            Memory = reader.GetDouble(2),
            Timestamp = reader.GetString(3)
        });
    }
    return Results.Ok(list);
});

// 장비별 최신 상태
app.MapGet("/api/devices", () =>
{
    using var conn = OpenDb();
    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT m.DeviceName, m.Cpu, m.Memory, m.Timestamp
        FROM Metrics m
        JOIN (SELECT DeviceName, MAX(Id) AS MaxId FROM Metrics GROUP BY DeviceName) x
          ON m.Id = x.MaxId
        ORDER BY m.DeviceName
        """;
    using var reader = cmd.ExecuteReader();
    var list = new List<object>();
    while (reader.Read())
    {
        list.Add(new
        {
            DeviceName = reader.GetString(0),
            Cpu = reader.GetDouble(1),
            Memory = reader.GetDouble(2),
            LastSeen = reader.GetString(3)
        });
    }
    return Results.Ok(list);
});

// 장비별 최근 기록 (그래프용, 오래된 순)
app.MapGet("/api/devices/{name}/history", (string name, int? limit) =>
{
    using var conn = OpenDb();
    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT Cpu, Memory, Timestamp FROM (
            SELECT Id, Cpu, Memory, Timestamp FROM Metrics
            WHERE DeviceName = $n ORDER BY Id DESC LIMIT $l
        ) ORDER BY Id ASC
        """;
    cmd.Parameters.AddWithValue("$n", name);
    cmd.Parameters.AddWithValue("$l", limit ?? 60);
    using var reader = cmd.ExecuteReader();
    var list = new List<object>();
    while (reader.Read())
    {
        list.Add(new
        {
            Cpu = reader.GetDouble(0),
            Memory = reader.GetDouble(1),
            Timestamp = reader.GetString(2)
        });
    }
    return Results.Ok(list);
});

// 알람 이력
app.MapGet("/api/alerts", (int? limit) =>
{
    using var conn = OpenDb();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT DeviceName, Type, Value, Threshold, Timestamp FROM Alerts ORDER BY Id DESC LIMIT $l";
    cmd.Parameters.AddWithValue("$l", limit ?? 20);
    using var reader = cmd.ExecuteReader();
    var list = new List<object>();
    while (reader.Read())
    {
        list.Add(new
        {
            DeviceName = reader.GetString(0),
            Type = reader.GetString(1),
            Value = reader.GetDouble(2),
            Threshold = reader.GetDouble(3),
            TimestampUtc = reader.GetString(4)
        });
    }
    return Results.Ok(list);
});

app.Run();

record MetricDto(string DeviceName, double Cpu, double Memory, DateTime Timestamp);

class MonitorHub : Hub { }






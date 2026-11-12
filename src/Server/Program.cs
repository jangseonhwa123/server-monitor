using Microsoft.AspNetCore.SignalR;
using MySqlConnector;

const double CpuThreshold = 80.0;
const int AlertCooldownSeconds = 60;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.AddSignalR();
var app = builder.Build();

// DB 연결 문자열은 설정(ConnectionStrings__Default)으로 받고, 없으면 개발용 기본값을 씁니다
var connectionString = app.Configuration.GetConnectionString("Default")
    ?? "Server=127.0.0.1;Port=3307;Database=monitor;User ID=monitor;Password=monitorpass";
connectionString = new MySqlConnectionStringBuilder(connectionString)
{
    DateTimeKind = MySqlDateTimeKind.Utc   // 읽어 온 시각이 UTC로 표시되도록 (JSON에 Z가 붙음)
}.ConnectionString;

async Task<MySqlConnection> OpenDbAsync()
{
    var conn = new MySqlConnection(connectionString);
    await conn.OpenAsync();
    return conn;
}

var schemaStatements = new[]
{
    """
    CREATE TABLE IF NOT EXISTS Metrics (
        Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
        DeviceName VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
        Cpu DOUBLE NOT NULL,
        Memory DOUBLE NOT NULL,
        Timestamp DATETIME(6) NOT NULL,
        INDEX idx_metrics_device_id (DeviceName, Id),
        INDEX idx_metrics_timestamp (Timestamp)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
    """,
    """
    CREATE TABLE IF NOT EXISTS Alerts (
        Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
        DeviceName VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
        Type VARCHAR(16) NOT NULL,
        Value DOUBLE NOT NULL,
        Threshold DOUBLE NOT NULL,
        Timestamp DATETIME(6) NOT NULL,
        INDEX idx_alerts_device_type_ts (DeviceName, Type, Timestamp),
        INDEX idx_alerts_timestamp (Timestamp)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
    """
};

// DB 컨테이너가 아직 준비 중일 수 있어서 재시도합니다
async Task InitSchemaAsync()
{
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await using var conn = await OpenDbAsync();
            foreach (var sql in schemaStatements)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync();
            }
            return;
        }
        catch (Exception ex) when (attempt < 30)
        {
            Console.WriteLine($"[DB] 연결 대기 중 ({attempt}/30): {ex.Message}");
            await Task.Delay(2000);
        }
    }
}

await InitSchemaAsync();

// 오래된 데이터 자동 삭제 (보존 기간은 MONITOR_RETENTION_DAYS, 기본 7일)
var retentionDays = int.TryParse(app.Configuration["MONITOR_RETENTION_DAYS"], out var parsedDays) && parsedDays > 0 ? parsedDays : 7;
_ = Task.Run(() => RetentionJob.RunAsync(OpenDbAsync, TimeSpan.FromDays(retentionDays), TimeSpan.FromHours(1), app.Lifetime.ApplicationStopping));

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

static int ClampLimit(int? limit, int fallback) => Math.Clamp(limit ?? fallback, 1, 1000);

app.MapPost("/api/metrics", async (MetricDto metric, IHubContext<MonitorHub> hub) =>
{
    var validationError = MetricValidator.Validate(metric);
    if (validationError != null)
    {
        return Results.BadRequest(new { error = validationError });
    }

    // 저장은 큐에 넣고 바로 응답합니다 (전담 쓰기 작업이 모아서 한 번에 저장)
    if (!MetricQueue.Enqueue(metric, OpenDbAsync))
    {
        return Results.StatusCode(503);
    }

    // 연결된 모든 관제 화면에 즉시 전달
    await hub.Clients.All.SendAsync("MetricReceived", metric);

    // CPU 임계값 초과 시 알람 기록 (같은 장비는 60초에 한 번만)
    if (metric.Cpu >= CpuThreshold)
    {
        await using var conn = await OpenDbAsync();

        await using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM Alerts WHERE DeviceName = @d AND Type = 'CPU' AND Timestamp > @since";
        check.Parameters.AddWithValue("@d", metric.DeviceName);
        check.Parameters.AddWithValue("@since", DateTime.UtcNow.AddSeconds(-AlertCooldownSeconds));
        var recent = Convert.ToInt64(await check.ExecuteScalarAsync());

        if (recent == 0)
        {
            var alertTime = DateTime.UtcNow;
            await using var alert = conn.CreateCommand();
            alert.CommandText = "INSERT INTO Alerts (DeviceName, Type, Value, Threshold, Timestamp) VALUES (@d, 'CPU', @v, @th, @ts)";
            alert.Parameters.AddWithValue("@d", metric.DeviceName);
            alert.Parameters.AddWithValue("@v", metric.Cpu);
            alert.Parameters.AddWithValue("@th", CpuThreshold);
            alert.Parameters.AddWithValue("@ts", alertTime);
            await alert.ExecuteNonQueryAsync();
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

app.MapGet("/api/metrics", async (int? limit) =>
{
    await using var conn = await OpenDbAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT DeviceName, Cpu, Memory, Timestamp FROM Metrics ORDER BY Id DESC LIMIT @l";
    cmd.Parameters.AddWithValue("@l", ClampLimit(limit, 10));
    await using var reader = await cmd.ExecuteReaderAsync();
    var list = new List<object>();
    while (await reader.ReadAsync())
    {
        list.Add(new
        {
            DeviceName = reader.GetString(0),
            Cpu = reader.GetDouble(1),
            Memory = reader.GetDouble(2),
            Timestamp = reader.GetDateTime(3)
        });
    }
    return Results.Ok(list);
});

// 장비별 최신 상태
app.MapGet("/api/devices", async () =>
{
    await using var conn = await OpenDbAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT m.DeviceName, m.Cpu, m.Memory, m.Timestamp
        FROM Metrics m
        JOIN (SELECT DeviceName, MAX(Id) AS MaxId FROM Metrics GROUP BY DeviceName) x
          ON m.Id = x.MaxId
        ORDER BY m.DeviceName
        """;
    await using var reader = await cmd.ExecuteReaderAsync();
    var list = new List<object>();
    while (await reader.ReadAsync())
    {
        list.Add(new
        {
            DeviceName = reader.GetString(0),
            Cpu = reader.GetDouble(1),
            Memory = reader.GetDouble(2),
            LastSeen = reader.GetDateTime(3)
        });
    }
    return Results.Ok(list);
});

// 장비별 최근 기록 (그래프용, 오래된 순)
app.MapGet("/api/devices/{name}/history", async (string name, int? limit) =>
{
    await using var conn = await OpenDbAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT Cpu, Memory, Timestamp FROM (
            SELECT Id, Cpu, Memory, Timestamp FROM Metrics
            WHERE DeviceName = @n ORDER BY Id DESC LIMIT @l
        ) recent ORDER BY Id ASC
        """;
    cmd.Parameters.AddWithValue("@n", name);
    cmd.Parameters.AddWithValue("@l", ClampLimit(limit, 60));
    await using var reader = await cmd.ExecuteReaderAsync();
    var list = new List<object>();
    while (await reader.ReadAsync())
    {
        list.Add(new
        {
            Cpu = reader.GetDouble(0),
            Memory = reader.GetDouble(1),
            Timestamp = reader.GetDateTime(2)
        });
    }
    return Results.Ok(list);
});

// 알람 이력
app.MapGet("/api/alerts", async (int? limit) =>
{
    await using var conn = await OpenDbAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT DeviceName, Type, Value, Threshold, Timestamp FROM Alerts ORDER BY Id DESC LIMIT @l";
    cmd.Parameters.AddWithValue("@l", ClampLimit(limit, 20));
    await using var reader = await cmd.ExecuteReaderAsync();
    var list = new List<object>();
    while (await reader.ReadAsync())
    {
        list.Add(new
        {
            DeviceName = reader.GetString(0),
            Type = reader.GetString(1),
            Value = reader.GetDouble(2),
            Threshold = reader.GetDouble(3),
            TimestampUtc = reader.GetDateTime(4)
        });
    }
    return Results.Ok(list);
});

app.Run();

record MetricDto(string DeviceName, double Cpu, double Memory, DateTime Timestamp);

class MonitorHub : Hub { }

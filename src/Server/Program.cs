using Microsoft.Data.Sqlite;

const double CpuThreshold = 80.0;
const int AlertCooldownSeconds = 60;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var dbPath = Path.Combine(builder.Environment.ContentRootPath, "monitor.db");
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

app.MapGet("/", () => "Server Monitor is running");

app.MapPost("/api/metrics", (MetricDto metric) =>
{
    using var conn = OpenDb();

    var insert = conn.CreateCommand();
    insert.CommandText = "INSERT INTO Metrics (DeviceName, Cpu, Memory, Timestamp) VALUES ($d, $c, $m, $t)";
    insert.Parameters.AddWithValue("$d", metric.DeviceName);
    insert.Parameters.AddWithValue("$c", metric.Cpu);
    insert.Parameters.AddWithValue("$m", metric.Memory);
    insert.Parameters.AddWithValue("$t", metric.Timestamp.ToString("o"));
    insert.ExecuteNonQuery();

    // CPU 임계값 초과 시 알람 기록 (같은 장비는 60초에 한 번만)
    if (metric.Cpu >= CpuThreshold)
    {
        var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM Alerts WHERE DeviceName = $d AND Type = 'CPU' AND Timestamp > $since";
        check.Parameters.AddWithValue("$d", metric.DeviceName);
        check.Parameters.AddWithValue("$since", DateTime.UtcNow.AddSeconds(-AlertCooldownSeconds).ToString("o"));
        var recent = (long)check.ExecuteScalar()!;

        if (recent == 0)
        {
            var alert = conn.CreateCommand();
            alert.CommandText = "INSERT INTO Alerts (DeviceName, Type, Value, Threshold, Timestamp) VALUES ($d, 'CPU', $v, $th, $ts)";
            alert.Parameters.AddWithValue("$d", metric.DeviceName);
            alert.Parameters.AddWithValue("$v", metric.Cpu);
            alert.Parameters.AddWithValue("$th", CpuThreshold);
            alert.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            alert.ExecuteNonQuery();
            Console.WriteLine($"[ALERT] {metric.DeviceName} CPU {metric.Cpu:F1}% (기준 {CpuThreshold}%)");
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

app.Run();

record MetricDto(string DeviceName, double Cpu, double Memory, DateTime Timestamp);


using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var dbPath = Path.Combine(builder.Environment.ContentRootPath, "monitor.db");
var connectionString = $"Data Source={dbPath}";

using (var conn = new SqliteConnection(connectionString))
{
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS Metrics (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            DeviceName TEXT NOT NULL,
            Cpu REAL NOT NULL,
            Memory REAL NOT NULL,
            Timestamp TEXT NOT NULL
        );
        """;
    cmd.ExecuteNonQuery();
}

app.MapGet("/", () => "Server Monitor is running");

app.MapPost("/api/metrics", (MetricDto metric) =>
{
    using var conn = new SqliteConnection(connectionString);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO Metrics (DeviceName, Cpu, Memory, Timestamp) VALUES ($d, $c, $m, $t)";
    cmd.Parameters.AddWithValue("$d", metric.DeviceName);
    cmd.Parameters.AddWithValue("$c", metric.Cpu);
    cmd.Parameters.AddWithValue("$m", metric.Memory);
    cmd.Parameters.AddWithValue("$t", metric.Timestamp.ToString("o"));
    cmd.ExecuteNonQuery();
    return Results.Ok(new { received = true });
});

app.MapGet("/api/metrics", (int? limit) =>
{
    using var conn = new SqliteConnection(connectionString);
    conn.Open();
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

app.Run();

record MetricDto(string DeviceName, double Cpu, double Memory, DateTime Timestamp);

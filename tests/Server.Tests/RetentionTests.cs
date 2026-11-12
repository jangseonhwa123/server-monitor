using Microsoft.Data.Sqlite;
using Xunit;

namespace Server.Tests;

public class RetentionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "retention-" + Guid.NewGuid().ToString("N") + ".db");

    public RetentionTests()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE Metrics (Id INTEGER PRIMARY KEY AUTOINCREMENT, DeviceName TEXT, Cpu REAL, Memory REAL, Timestamp TEXT);
            CREATE TABLE Alerts (Id INTEGER PRIMARY KEY AUTOINCREMENT, DeviceName TEXT, Type TEXT, Value REAL, Threshold REAL, Timestamp TEXT);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_path};Pooling=False");
        conn.Open();
        return conn;
    }

    private void InsertMetric(string timestamp)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Metrics (DeviceName, Cpu, Memory, Timestamp) VALUES ('d', 1, 1, $t)";
        cmd.Parameters.AddWithValue("$t", timestamp);
        cmd.ExecuteNonQuery();
    }

    private void InsertAlert(string timestamp)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Alerts (DeviceName, Type, Value, Threshold, Timestamp) VALUES ('d', 'CPU', 90, 80, $t)";
        cmd.Parameters.AddWithValue("$t", timestamp);
        cmd.ExecuteNonQuery();
    }

    private long Count(string table)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void Purge_deletes_only_rows_older_than_cutoff_regardless_of_timezone_format()
    {
        var now = DateTime.UtcNow;
        var kst = TimeSpan.FromHours(9);

        // 지표: 오래된 것 2건(UTC 표기, +09:00 표기), 최근 것 2건(UTC 표기, +09:00 표기)
        InsertMetric(now.AddDays(-10).ToString("o"));
        InsertMetric(new DateTimeOffset(now.AddDays(-10)).ToOffset(kst).ToString("o"));
        InsertMetric(now.AddHours(-1).ToString("o"));
        InsertMetric(new DateTimeOffset(now.AddHours(-1)).ToOffset(kst).ToString("o"));

        // 알람: 오래된 것 1건, 최근 것 1건
        InsertAlert(now.AddDays(-10).ToString("o"));
        InsertAlert(now.AddHours(-1).ToString("o"));

        var (metrics, alerts) = RetentionJob.Purge(Open, now.AddDays(-7));

        Assert.Equal(2, metrics);
        Assert.Equal(1, alerts);
        Assert.Equal(2, Count("Metrics"));
        Assert.Equal(1, Count("Alerts"));
    }

    [Fact]
    public void Purge_on_empty_tables_deletes_nothing()
    {
        var (metrics, alerts) = RetentionJob.Purge(Open, DateTime.UtcNow);

        Assert.Equal(0, metrics);
        Assert.Equal(0, alerts);
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }
}

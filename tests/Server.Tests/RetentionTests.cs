using MySqlConnector;
using Xunit;

namespace Server.Tests;

[Collection("api")]
public class RetentionTests
{
    private readonly ApiFactory _factory;

    public RetentionTests(ApiFactory factory) => _factory = factory;

    // 서버를 한 번 띄워서 테이블이 만들어진 뒤에 연결합니다
    private async Task<MySqlConnection> OpenAsync()
    {
        _factory.CreateClient();
        var cs = new MySqlConnectionStringBuilder(_factory.ConnectionString) { DateTimeKind = MySqlDateTimeKind.Utc }.ConnectionString;
        var conn = new MySqlConnection(cs);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(MySqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(MySqlConnection conn, string table, string device)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE DeviceName = @d";
        cmd.Parameters.AddWithValue("@d", device);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Purge_deletes_only_rows_older_than_cutoff()
    {
        await using var conn = await OpenAsync();
        var now = DateTime.UtcNow;

        const string insertMetric = "INSERT INTO Metrics (DeviceName, Cpu, Memory, Timestamp) VALUES (@d, 1, 1, @t)";
        await ExecAsync(conn, insertMetric, ("@d", "ret-old"), ("@t", now.AddDays(-10)));
        await ExecAsync(conn, insertMetric, ("@d", "ret-new"), ("@t", now.AddHours(-1)));

        const string insertAlert = "INSERT INTO Alerts (DeviceName, Type, Value, Threshold, Timestamp) VALUES (@d, 'CPU', 90, 80, @t)";
        await ExecAsync(conn, insertAlert, ("@d", "ret-old"), ("@t", now.AddDays(-10)));
        await ExecAsync(conn, insertAlert, ("@d", "ret-new"), ("@t", now.AddHours(-1)));

        var (metrics, alerts) = await RetentionJob.PurgeAsync(OpenAsync, now.AddDays(-7));

        Assert.True(metrics >= 1);
        Assert.True(alerts >= 1);
        Assert.Equal(0, await CountAsync(conn, "Metrics", "ret-old"));
        Assert.Equal(1, await CountAsync(conn, "Metrics", "ret-new"));
        Assert.Equal(0, await CountAsync(conn, "Alerts", "ret-old"));
        Assert.Equal(1, await CountAsync(conn, "Alerts", "ret-new"));
    }

    [Fact]
    public async Task Purge_removes_more_rows_than_a_single_batch()
    {
        await using var conn = await OpenAsync();
        var old = DateTime.UtcNow.AddDays(-30);

        // 한 번에 지우는 단위(1만 건)를 넘는 행을 만들어 반복 삭제가 동작하는지 확인합니다
        await ExecAsync(conn, "SET SESSION cte_max_recursion_depth = 20000");
        await ExecAsync(conn,
            "INSERT INTO Metrics (DeviceName, Cpu, Memory, Timestamp) " +
            "WITH RECURSIVE seq AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM seq WHERE n < 10500) " +
            "SELECT 'ret-bulk', 1, 1, @t FROM seq",
            ("@t", old));
        Assert.Equal(10500, await CountAsync(conn, "Metrics", "ret-bulk"));

        var (metrics, _) = await RetentionJob.PurgeAsync(OpenAsync, DateTime.UtcNow.AddDays(-7));

        Assert.True(metrics >= 10500);
        Assert.Equal(0, await CountAsync(conn, "Metrics", "ret-bulk"));
    }
}

using Microsoft.Data.Sqlite;

// 보존 기간이 지난 지표와 알람을 주기적으로 삭제합니다
public static class RetentionJob
{
    public static async Task RunAsync(Func<SqliteConnection> openDb, TimeSpan keep, TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (metrics, alerts) = Purge(openDb, DateTime.UtcNow - keep);
                if (metrics + alerts > 0)
                {
                    Console.WriteLine($"[RETENTION] 지표 {metrics}건, 알람 {alerts}건을 삭제했습니다");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RETENTION] 정리 실패: {ex.Message}");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(ct)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // cutoffUtc보다 오래된 행을 삭제하고 삭제한 건수를 돌려줍니다
    public static (int Metrics, int Alerts) Purge(Func<SqliteConnection> openDb, DateTime cutoffUtc)
    {
        var cutoff = cutoffUtc.ToUniversalTime().ToString("o");
        using var conn = openDb();

        int metrics = Delete(conn, "DELETE FROM Metrics WHERE datetime(Timestamp) < datetime($cutoff)", cutoff);
        int alerts = Delete(conn, "DELETE FROM Alerts WHERE datetime(Timestamp) < datetime($cutoff)", cutoff);
        return (metrics, alerts);
    }

    private static int Delete(SqliteConnection conn, string sql, string cutoff)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        return cmd.ExecuteNonQuery();
    }
}

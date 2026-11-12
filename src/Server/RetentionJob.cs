using MySqlConnector;

// 보존 기간이 지난 지표와 알람을 주기적으로 삭제합니다
public static class RetentionJob
{
    private const int BatchSize = 10_000;

    public static async Task RunAsync(Func<Task<MySqlConnection>> openDb, TimeSpan keep, TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (metrics, alerts) = await PurgeAsync(openDb, DateTime.UtcNow - keep);
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
    public static async Task<(long Metrics, long Alerts)> PurgeAsync(Func<Task<MySqlConnection>> openDb, DateTime cutoffUtc)
    {
        var cutoff = cutoffUtc.ToUniversalTime();
        await using var conn = await openDb();

        var metrics = await DeleteInBatchesAsync(conn, "Metrics", cutoff);
        var alerts = await DeleteInBatchesAsync(conn, "Alerts", cutoff);
        return (metrics, alerts);
    }

    // 오래 잠그지 않도록 일정 건수씩 나눠서 지웁니다
    private static async Task<long> DeleteInBatchesAsync(MySqlConnection conn, string table, DateTime cutoff)
    {
        long total = 0;
        int deleted;
        do
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {table} WHERE Timestamp < @cutoff LIMIT {BatchSize}";
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            deleted = await cmd.ExecuteNonQueryAsync();
            total += deleted;
        } while (deleted == BatchSize);
        return total;
    }
}

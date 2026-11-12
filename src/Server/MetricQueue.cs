using System.Threading.Channels;
using Microsoft.Data.Sqlite;

// 요청 스레드는 큐에 넣기만 하고, 전담 쓰기 작업 하나가 모아서 한 번에 저장합니다
internal static class MetricQueue
{
    private const int Capacity = 100_000;
    private const int MaxBatch = 1000;

    private static readonly Channel<MetricDto> Queue =
        Channel.CreateBounded<MetricDto>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

    private static int _started;

    // 큐가 가득 차면 false (호출 쪽이 503을 돌려주면 에이전트가 버퍼에 보관했다가 재전송합니다)
    public static bool Enqueue(MetricDto metric, Func<SqliteConnection> openDb)
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _ = Task.Run(() => WriteLoopAsync(openDb));
        }
        return Queue.Writer.TryWrite(metric);
    }

    private static async Task WriteLoopAsync(Func<SqliteConnection> openDb)
    {
        var reader = Queue.Reader;
        var batch = new List<MetricDto>(MaxBatch);

        while (await reader.WaitToReadAsync())
        {
            batch.Clear();
            while (batch.Count < MaxBatch && reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            try
            {
                InsertBatch(batch, openDb);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] 배치 저장 실패 ({batch.Count}건): {ex.Message}");
            }
        }
    }

    private static void InsertBatch(List<MetricDto> batch, Func<SqliteConnection> openDb)
    {
        using var conn = openDb();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO Metrics (DeviceName, Cpu, Memory, Timestamp) VALUES ($d, $c, $m, $t)";
        var pd = cmd.Parameters.Add("$d", SqliteType.Text);
        var pc = cmd.Parameters.Add("$c", SqliteType.Real);
        var pm = cmd.Parameters.Add("$m", SqliteType.Real);
        var pt = cmd.Parameters.Add("$t", SqliteType.Text);

        foreach (var m in batch)
        {
            pd.Value = m.DeviceName;
            pc.Value = m.Cpu;
            pm.Value = m.Memory;
            pt.Value = m.Timestamp.ToString("o");
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }
}

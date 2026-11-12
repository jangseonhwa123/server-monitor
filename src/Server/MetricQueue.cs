using System.Text;
using System.Threading.Channels;
using MySqlConnector;

// 요청 스레드는 큐에 넣기만 하고, 전담 쓰기 작업 하나가 모아서 한 번에 저장합니다
internal static class MetricQueue
{
    private const int Capacity = 100_000;
    private const int MaxBatch = 1000;
    private const int RowsPerStatement = 500;

    private static readonly Channel<MetricDto> Queue =
        Channel.CreateBounded<MetricDto>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

    private static int _started;

    // 큐가 가득 차면 false (호출 쪽이 503을 돌려주면 에이전트가 버퍼에 보관했다가 재전송합니다)
    public static bool Enqueue(MetricDto metric, Func<Task<MySqlConnection>> openDb)
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _ = Task.Run(() => WriteLoopAsync(openDb));
        }
        return Queue.Writer.TryWrite(metric);
    }

    private static async Task WriteLoopAsync(Func<Task<MySqlConnection>> openDb)
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
                await InsertBatchAsync(batch, openDb);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] 배치 저장 실패 ({batch.Count}건): {ex.Message}");
            }
        }
    }

    private static async Task InsertBatchAsync(List<MetricDto> batch, Func<Task<MySqlConnection>> openDb)
    {
        await using var conn = await openDb();
        await using var tx = await conn.BeginTransactionAsync();

        for (var offset = 0; offset < batch.Count; offset += RowsPerStatement)
        {
            var count = Math.Min(RowsPerStatement, batch.Count - offset);
            var sql = new StringBuilder("INSERT INTO Metrics (DeviceName, Cpu, Memory, Timestamp) VALUES ");
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            for (var i = 0; i < count; i++)
            {
                var m = batch[offset + i];
                if (i > 0) sql.Append(',');
                sql.Append($"(@d{i},@c{i},@m{i},@t{i})");
                cmd.Parameters.AddWithValue($"@d{i}", m.DeviceName);
                cmd.Parameters.AddWithValue($"@c{i}", m.Cpu);
                cmd.Parameters.AddWithValue($"@m{i}", m.Memory);
                cmd.Parameters.AddWithValue($"@t{i}", m.Timestamp.ToUniversalTime());
            }

            cmd.CommandText = sql.ToString();
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }
}

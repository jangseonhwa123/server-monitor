using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Server.Tests;

// 테스트마다 임시 DB를 쓰는 서버 인스턴스
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-key";
    private readonly string _dir;

    public ApiFactory()
    {
        _dir = Path.Combine(Path.GetTempPath(), "servermonitor-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("MONITOR_DB_PATH", Path.Combine(_dir, "test.db"));
        Environment.SetEnvironmentVariable("MONITOR_API_KEY", ApiKey);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }
}

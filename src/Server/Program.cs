var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "Server Monitor is running");

app.MapPost("/api/metrics", (MetricDto metric) =>
{
    Console.WriteLine(
        $"[{metric.Timestamp:HH:mm:ss}] {metric.DeviceName}  CPU {metric.Cpu:F1}%  메모리 {metric.Memory:F1}%");
    return Results.Ok(new { received = true });
});

app.Run();

record MetricDto(string DeviceName, double Cpu, double Memory, DateTime Timestamp);

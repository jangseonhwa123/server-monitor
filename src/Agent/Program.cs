using System.Diagnostics;

var cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total");
var mem = new PerformanceCounter("Memory", "% Committed Bytes In Use");

cpu.NextValue(); // 첫 호출은 항상 0이라 버립니다

while (true)
{
    await Task.Delay(1000);
    Console.WriteLine(
        $"{DateTime.Now:HH:mm:ss}  CPU {cpu.NextValue():F1}%  메모리 {mem.NextValue():F1}%");
}
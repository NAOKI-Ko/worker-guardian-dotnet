using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using WorkerGuardian;

BenchmarkSwitcher.FromAssembly(typeof(GuardianBenchmarks).Assembly).Run(args);

[MemoryDiagnoser]
public class GuardianBenchmarks
{
    private readonly RestartPolicy policy = new(
        initialDelay: TimeSpan.FromMilliseconds(10),
        maxDelay: TimeSpan.FromMinutes(1),
        jitter: 0.2);
    private readonly InMemoryGuardianMetrics metrics = new();
    private readonly WorkerId workerId = new("benchmark");

    [Benchmark(Baseline = true)]
    public TimeSpan CalculateBackoff() => policy.DelayFor(8, 0.42);

    [Benchmark]
    public long RecordMetric()
    {
        metrics.Increment("worker.runs", workerId);
        return metrics.Count("worker.runs", workerId);
    }
}

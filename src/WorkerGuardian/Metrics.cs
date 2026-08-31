using System.Collections.Concurrent;

namespace WorkerGuardian;

public interface IGuardianMetrics
{
    void Increment(string name, WorkerId workerId);

    void Record(string name, double value, WorkerId workerId);
}

public sealed class InMemoryGuardianMetrics : IGuardianMetrics
{
    private readonly ConcurrentDictionary<(string Name, WorkerId WorkerId), long> counters = new();
    private readonly ConcurrentDictionary<(string Name, WorkerId WorkerId), ConcurrentQueue<double>> values = new();

    public void Increment(string name, WorkerId workerId) =>
        counters.AddOrUpdate((name, workerId), 1, static (_, current) => current + 1);

    public void Record(string name, double value, WorkerId workerId) =>
        values.GetOrAdd((name, workerId), static _ => new ConcurrentQueue<double>()).Enqueue(value);

    public long Count(string name, WorkerId workerId) => counters.GetValueOrDefault((name, workerId));

    public IReadOnlyList<double> Values(string name, WorkerId workerId) =>
        values.TryGetValue((name, workerId), out var measurements) ? measurements.ToArray() : [];
}

internal sealed class NullGuardianMetrics : IGuardianMetrics
{
    public void Increment(string name, WorkerId workerId)
    {
    }

    public void Record(string name, double value, WorkerId workerId)
    {
    }
}


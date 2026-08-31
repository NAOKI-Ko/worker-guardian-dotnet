namespace WorkerGuardian;

public interface IGuardianWorker
{
    ValueTask ExecuteAsync(WorkerExecutionContext context, CancellationToken cancellationToken);

    ValueTask<WorkerHealth> CheckHealthAsync(CancellationToken cancellationToken);
}

public sealed class WorkerExecutionContext
{
    private readonly Action heartbeat;

    internal WorkerExecutionContext(WorkerId workerId, int attempt, Action heartbeat)
    {
        WorkerId = workerId;
        Attempt = attempt;
        this.heartbeat = heartbeat;
    }

    public WorkerId WorkerId { get; }

    public int Attempt { get; }

    public void Heartbeat() => heartbeat();
}

public sealed record WorkerRegistration(
    WorkerId WorkerId,
    Func<IServiceProvider, IGuardianWorker> Factory,
    RestartPolicy RestartPolicy);

public interface IWorkerGuardian
{
    IReadOnlyDictionary<WorkerId, WorkerSnapshot> Snapshots { get; }

    IAsyncEnumerable<GuardianEvent> Events(CancellationToken cancellationToken = default);

    ValueTask<WorkerHealth> CheckHealthAsync(CancellationToken cancellationToken = default);
}


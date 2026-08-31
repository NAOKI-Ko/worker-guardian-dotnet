using System.Collections.Immutable;

namespace WorkerGuardian;

public readonly record struct WorkerId : IComparable<WorkerId>
{
    public WorkerId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Worker id cannot be blank.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public int CompareTo(WorkerId other) => StringComparer.Ordinal.Compare(Value, other.Value);

    public override string ToString() => Value;
}

public enum WorkerState
{
    Registered,
    Starting,
    Running,
    Unhealthy,
    Restarting,
    Stopping,
    Stopped,
    Failed,
}

public enum WorkerHealthStatus
{
    Healthy,
    Degraded,
    Unhealthy,
}

public sealed record WorkerHealth(WorkerHealthStatus Status, string Detail = "");

public sealed record RestartPolicy
{
    public RestartPolicy(
        int maxRestarts = 3,
        TimeSpan? initialDelay = null,
        TimeSpan? maxDelay = null,
        double multiplier = 2,
        double jitter = 0.1,
        TimeSpan? heartbeatTimeout = null,
        int circuitFailureThreshold = 3,
        TimeSpan? circuitRecoveryTimeout = null)
    {
        MaxRestarts = maxRestarts;
        InitialDelay = initialDelay ?? TimeSpan.FromMilliseconds(50);
        MaxDelay = maxDelay ?? TimeSpan.FromSeconds(5);
        Multiplier = multiplier;
        Jitter = jitter;
        HeartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(30);
        CircuitFailureThreshold = circuitFailureThreshold;
        CircuitRecoveryTimeout = circuitRecoveryTimeout ?? TimeSpan.FromSeconds(30);
        Validate();
    }

    public int MaxRestarts { get; }

    public TimeSpan InitialDelay { get; }

    public TimeSpan MaxDelay { get; }

    public double Multiplier { get; }

    public double Jitter { get; }

    public TimeSpan HeartbeatTimeout { get; }

    public int CircuitFailureThreshold { get; }

    public TimeSpan CircuitRecoveryTimeout { get; }

    public TimeSpan DelayFor(int restart, double randomValue)
    {
        var exponential = InitialDelay.TotalMilliseconds * Math.Pow(Multiplier, Math.Max(0, restart - 1));
        var bounded = Math.Min(MaxDelay.TotalMilliseconds, exponential);
        var factor = 1 + (Jitter * ((2 * randomValue) - 1));
        return TimeSpan.FromMilliseconds(Math.Clamp(bounded * factor, 0, MaxDelay.TotalMilliseconds));
    }

    private void Validate()
    {
        if (MaxRestarts < 0 || InitialDelay < TimeSpan.Zero || MaxDelay < InitialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRestarts), "Restart counts and delays are invalid.");
        }

        if (Multiplier < 1 || Jitter is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Multiplier), "Multiplier or jitter is invalid.");
        }

        if (HeartbeatTimeout <= TimeSpan.Zero || CircuitFailureThreshold < 1 || CircuitRecoveryTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(HeartbeatTimeout), "Timeouts and thresholds are invalid.");
        }
    }
}

public sealed record WorkerSnapshot(
    WorkerId WorkerId,
    WorkerState State,
    int Attempt,
    DateTimeOffset? LastHeartbeat,
    string Detail,
    DateTimeOffset UpdatedAt)
{
    public static WorkerSnapshot Registered(WorkerId id, DateTimeOffset now, string detail = "") =>
        new(id, WorkerState.Registered, 0, null, detail, now);

    public WorkerSnapshot Transition(WorkerState target, DateTimeOffset now, string detail = "")
    {
        if (!AllowedTransitions.TryGetValue(State, out var allowed) || !allowed.Contains(target))
        {
            throw new InvalidOperationException($"Invalid worker transition: {State} -> {target}.");
        }

        return this with { State = target, Detail = detail, UpdatedAt = now };
    }

    private static readonly ImmutableDictionary<WorkerState, ImmutableHashSet<WorkerState>> AllowedTransitions =
        new Dictionary<WorkerState, WorkerState[]>
        {
            [WorkerState.Registered] = [WorkerState.Starting, WorkerState.Stopped],
            [WorkerState.Starting] = [WorkerState.Running, WorkerState.Failed, WorkerState.Stopping],
            [WorkerState.Running] = [WorkerState.Unhealthy, WorkerState.Restarting, WorkerState.Stopping, WorkerState.Failed],
            [WorkerState.Unhealthy] = [WorkerState.Running, WorkerState.Restarting, WorkerState.Stopping, WorkerState.Failed],
            [WorkerState.Restarting] = [WorkerState.Starting, WorkerState.Stopping, WorkerState.Failed],
            [WorkerState.Stopping] = [WorkerState.Stopped],
            [WorkerState.Stopped] = [WorkerState.Starting],
            [WorkerState.Failed] = [WorkerState.Starting],
        }.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutableHashSet());
}

public sealed class GuardianOptions
{
    public int MaxConcurrency { get; set; } = 16;

    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan MonitorInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    public int EventCapacity { get; set; } = 512;

    public string DatabasePath { get; set; } = "worker-guardian.db";

    internal bool IsValid() =>
        MaxConcurrency > 0 &&
        ShutdownTimeout > TimeSpan.Zero &&
        MonitorInterval > TimeSpan.Zero &&
        EventCapacity > 0 &&
        !string.IsNullOrWhiteSpace(DatabasePath);
}


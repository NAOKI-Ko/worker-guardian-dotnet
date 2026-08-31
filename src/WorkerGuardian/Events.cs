namespace WorkerGuardian;

public abstract record GuardianEvent(WorkerId WorkerId, DateTimeOffset OccurredAt);

public sealed record WorkerStarted(WorkerId WorkerId, int Attempt, DateTimeOffset OccurredAt)
    : GuardianEvent(WorkerId, OccurredAt);

public sealed record WorkerStopped(WorkerId WorkerId, DateTimeOffset OccurredAt)
    : GuardianEvent(WorkerId, OccurredAt);

public sealed record WorkerFailed(WorkerId WorkerId, FailureInfo Failure, int Attempt, DateTimeOffset OccurredAt)
    : GuardianEvent(WorkerId, OccurredAt);

public sealed record WorkerRestarted(WorkerId WorkerId, int NextAttempt, TimeSpan Delay, DateTimeOffset OccurredAt)
    : GuardianEvent(WorkerId, OccurredAt);

public sealed record WorkerUnhealthy(WorkerId WorkerId, string Reason, DateTimeOffset OccurredAt)
    : GuardianEvent(WorkerId, OccurredAt);

public sealed record WorkerRecovered(WorkerId WorkerId, DateTimeOffset OccurredAt)
    : GuardianEvent(WorkerId, OccurredAt);

public sealed record FailureInfo(string Type, string Message)
{
    public static FailureInfo From(Exception error) => new(error.GetType().Name, error.Message);
}


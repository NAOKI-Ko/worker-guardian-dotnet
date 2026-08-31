namespace WorkerGuardian;

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen,
}

public sealed class CircuitBreaker
{
    private readonly int failureThreshold;
    private readonly TimeSpan recoveryTimeout;
    private readonly TimeProvider timeProvider;
    private readonly Lock gate = new();
    private int failures;
    private DateTimeOffset? openedAt;

    public CircuitBreaker(int failureThreshold, TimeSpan recoveryTimeout, TimeProvider? timeProvider = null)
    {
        if (failureThreshold < 1 || recoveryTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(failureThreshold));
        }

        this.failureThreshold = failureThreshold;
        this.recoveryTimeout = recoveryTimeout;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public CircuitState State
    {
        get
        {
            lock (gate)
            {
                return openedAt switch
                {
                    null => CircuitState.Closed,
                    { } opened when timeProvider.GetUtcNow() - opened >= recoveryTimeout => CircuitState.HalfOpen,
                    _ => CircuitState.Open,
                };
            }
        }
    }

    public async ValueTask<T> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (gate)
        {
            if (State is CircuitState.Open)
            {
                throw new CircuitOpenException();
            }
        }

        try
        {
            var result = await operation(cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                failures = 0;
                openedAt = null;
            }

            return result;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            lock (gate)
            {
                failures++;
                if (failures >= failureThreshold)
                {
                    openedAt = timeProvider.GetUtcNow();
                }
            }

            throw;
        }
    }
}

public sealed class CircuitOpenException : InvalidOperationException
{
    public CircuitOpenException()
        : base("The worker circuit is open.")
    {
    }
}


using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WorkerGuardian;

public static class WorkerGuardianDiagnostics
{
    public const string ActivitySourceName = "WorkerGuardian";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}

public sealed class WorkerGuardianService : BackgroundService, IWorkerGuardian
{
    private readonly IReadOnlyList<WorkerRegistration> registrations;
    private readonly IServiceProvider services;
    private readonly IWorkerStateRepository repository;
    private readonly EventPipeline<GuardianEvent> events;
    private readonly IGuardianMetrics metrics;
    private readonly GuardianOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<WorkerGuardianService> logger;
    private readonly ConcurrentDictionary<WorkerId, WorkerSnapshot> snapshots = new();
    private readonly SemaphoreSlim concurrency;

    public WorkerGuardianService(
        IEnumerable<WorkerRegistration> registrations,
        IServiceProvider services,
        IWorkerStateRepository repository,
        EventPipeline<GuardianEvent> events,
        IGuardianMetrics metrics,
        IOptions<GuardianOptions> options,
        TimeProvider timeProvider,
        ILogger<WorkerGuardianService> logger)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var orderedRegistrations = registrations.OrderBy(registration => registration.WorkerId).ToArray();
        var duplicate = orderedRegistrations
            .GroupBy(registration => registration.WorkerId)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate worker id: {duplicate.Key}.", nameof(registrations));
        }

        this.registrations = orderedRegistrations;
        this.services = services;
        this.repository = repository;
        this.events = events;
        this.metrics = metrics;
        this.options = options.Value;
        this.timeProvider = timeProvider;
        this.logger = logger;
        concurrency = new SemaphoreSlim(this.options.MaxConcurrency, this.options.MaxConcurrency);
    }

    public IReadOnlyDictionary<WorkerId, WorkerSnapshot> Snapshots => snapshots;

    public IAsyncEnumerable<GuardianEvent> Events(CancellationToken cancellationToken = default) =>
        events.ReadAllAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await repository.InitializeAsync(stoppingToken).ConfigureAwait(false);
        var recovered = (await repository.LoadAllAsync(stoppingToken).ConfigureAwait(false))
            .ToDictionary(snapshot => snapshot.WorkerId);
        foreach (var registration in registrations)
        {
            var detail = recovered.TryGetValue(registration.WorkerId, out var previous)
                ? $"Recovered from {previous.State}."
                : string.Empty;
            var snapshot = WorkerSnapshot.Registered(registration.WorkerId, timeProvider.GetUtcNow(), detail);
            snapshots[registration.WorkerId] = snapshot;
            await repository.SaveAsync(snapshot, stoppingToken).ConfigureAwait(false);
        }

        await Task.WhenAll(registrations.Select(registration => SuperviseAsync(registration, stoppingToken)))
            .ConfigureAwait(false);
    }

    public async ValueTask<WorkerHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var results = new ConcurrentBag<WorkerHealth>();
        await Parallel.ForEachAsync(registrations, cancellationToken, async (registration, token) =>
        {
            try
            {
                var worker = registration.Factory(services);
                results.Add(await worker.CheckHealthAsync(token).ConfigureAwait(false));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                results.Add(new WorkerHealth(WorkerHealthStatus.Unhealthy, error.Message));
            }
        }).ConfigureAwait(false);

        var statuses = results.Select(result => result.Status).ToHashSet();
        return statuses switch
        {
            var set when set.Contains(WorkerHealthStatus.Unhealthy) =>
                new WorkerHealth(WorkerHealthStatus.Unhealthy, "One or more workers are unhealthy."),
            var set when set.Contains(WorkerHealthStatus.Degraded) =>
                new WorkerHealth(WorkerHealthStatus.Degraded, "One or more workers are degraded."),
            _ => new WorkerHealth(WorkerHealthStatus.Healthy, $"{results.Count} workers are healthy."),
        };
    }

    public override void Dispose()
    {
        concurrency.Dispose();
        base.Dispose();
    }

    private async Task SuperviseAsync(WorkerRegistration registration, CancellationToken stoppingToken)
    {
        var restart = 0;
        var recovering = false;
        var breaker = new CircuitBreaker(
            registration.RestartPolicy.CircuitFailureThreshold,
            registration.RestartPolicy.CircuitRecoveryTimeout,
            timeProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            var attempt = restart + 1;
            await TransitionAsync(registration.WorkerId, WorkerState.Starting, attempt, string.Empty, stoppingToken)
                .ConfigureAwait(false);
            try
            {
                var worker = registration.Factory(services);
                await TransitionAsync(registration.WorkerId, WorkerState.Running, attempt, string.Empty, stoppingToken)
                    .ConfigureAwait(false);
                await events.PublishAsync(
                    new WorkerStarted(registration.WorkerId, attempt, timeProvider.GetUtcNow()),
                    stoppingToken).ConfigureAwait(false);
                if (recovering)
                {
                    await events.PublishAsync(
                        new WorkerRecovered(registration.WorkerId, timeProvider.GetUtcNow()),
                        stoppingToken).ConfigureAwait(false);
                    recovering = false;
                }

                using var activity = WorkerGuardianDiagnostics.ActivitySource.StartActivity("worker.run");
                activity?.SetTag("worker.id", registration.WorkerId.Value);
                activity?.SetTag("worker.attempt", attempt);
                var started = timeProvider.GetTimestamp();
                await concurrency.WaitAsync(stoppingToken).ConfigureAwait(false);
                try
                {
                    await breaker.ExecuteAsync(
                        async token =>
                        {
                            await ExecuteMonitoredAsync(worker, registration, attempt, token).ConfigureAwait(false);
                            return true;
                        },
                        stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    concurrency.Release();
                    metrics.Record(
                        "worker.run.seconds",
                        timeProvider.GetElapsedTime(started).TotalSeconds,
                        registration.WorkerId);
                }

                await StopWorkerAsync(registration.WorkerId, attempt, stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await StopWorkerAsync(registration.WorkerId, attempt, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (Exception error)
            {
                metrics.Increment("worker.failures", registration.WorkerId);
                await events.PublishAsync(
                    new WorkerFailed(registration.WorkerId, FailureInfo.From(error), attempt, timeProvider.GetUtcNow()),
                    CancellationToken.None).ConfigureAwait(false);
                logger.LogError(error, "Worker {WorkerId} failed on attempt {Attempt}", registration.WorkerId, attempt);

                if (restart >= registration.RestartPolicy.MaxRestarts ||
                    snapshots[registration.WorkerId].State is WorkerState.Starting)
                {
                    await TransitionAsync(
                        registration.WorkerId,
                        WorkerState.Failed,
                        attempt,
                        error.Message,
                        CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                restart++;
                recovering = true;
                var delay = registration.RestartPolicy.DelayFor(restart, Random.Shared.NextDouble());
                await TransitionAsync(
                    registration.WorkerId,
                    WorkerState.Restarting,
                    attempt,
                    error.Message,
                    CancellationToken.None).ConfigureAwait(false);
                await events.PublishAsync(
                    new WorkerRestarted(registration.WorkerId, restart + 1, delay, timeProvider.GetUtcNow()),
                    CancellationToken.None).ConfigureAwait(false);
                metrics.Increment("worker.restarts", registration.WorkerId);
                await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask ExecuteMonitoredAsync(
        IGuardianWorker worker,
        WorkerRegistration registration,
        int attempt,
        CancellationToken stoppingToken)
    {
        using var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeatTimestamp = timeProvider.GetTimestamp();
        var context = new WorkerExecutionContext(registration.WorkerId, attempt, () =>
        {
            Interlocked.Exchange(ref heartbeatTimestamp, timeProvider.GetTimestamp());
            snapshots.AddOrUpdate(
                registration.WorkerId,
                _ => throw new InvalidOperationException("Worker snapshot is missing."),
                (_, snapshot) => snapshot with { LastHeartbeat = timeProvider.GetUtcNow() });
        });
        context.Heartbeat();
        var execution = worker.ExecuteAsync(context, workerCancellation.Token).AsTask();

        while (!execution.IsCompleted)
        {
            var tick = Task.Delay(options.MonitorInterval, timeProvider, stoppingToken);
            if (await Task.WhenAny(execution, tick).ConfigureAwait(false) == execution)
            {
                break;
            }

            await tick.ConfigureAwait(false);
            if (timeProvider.GetElapsedTime(Interlocked.Read(ref heartbeatTimestamp)) >
                registration.RestartPolicy.HeartbeatTimeout)
            {
                await TransitionAsync(
                    registration.WorkerId,
                    WorkerState.Unhealthy,
                    attempt,
                    "Heartbeat timed out.",
                    stoppingToken).ConfigureAwait(false);
                await events.PublishAsync(
                    new WorkerUnhealthy(registration.WorkerId, "Heartbeat timed out.", timeProvider.GetUtcNow()),
                    stoppingToken).ConfigureAwait(false);
                metrics.Increment("worker.unhealthy", registration.WorkerId);
                workerCancellation.Cancel();
                try
                {
                    await execution.WaitAsync(options.ShutdownTimeout, TimeProvider.System).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                throw new TimeoutException("Worker heartbeat timed out.");
            }
        }

        await execution.ConfigureAwait(false);
    }

    private async ValueTask TransitionAsync(
        WorkerId workerId,
        WorkerState target,
        int attempt,
        string detail,
        CancellationToken cancellationToken)
    {
        var snapshot = snapshots[workerId].Transition(target, timeProvider.GetUtcNow(), detail) with
        {
            Attempt = attempt,
        };
        snapshots[workerId] = snapshot;
        await repository.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Worker {WorkerId} transitioned to {State}", workerId, target);
    }

    private async ValueTask StopWorkerAsync(
        WorkerId workerId,
        int attempt,
        CancellationToken cancellationToken)
    {
        var state = snapshots[workerId].State;
        if (state is WorkerState.Stopped or WorkerState.Failed)
        {
            return;
        }

        await TransitionAsync(workerId, WorkerState.Stopping, attempt, string.Empty, cancellationToken)
            .ConfigureAwait(false);
        await TransitionAsync(workerId, WorkerState.Stopped, attempt, string.Empty, cancellationToken)
            .ConfigureAwait(false);
        await events.PublishAsync(new WorkerStopped(workerId, timeProvider.GetUtcNow()), CancellationToken.None)
            .ConfigureAwait(false);
    }
}

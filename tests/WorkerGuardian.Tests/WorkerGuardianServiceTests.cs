using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace WorkerGuardian.Tests;

public sealed class WorkerGuardianServiceTests
{
    [Fact]
    public async Task DuplicateWorkerIdentifiersAreRejected()
    {
        var duplicate = Registration("duplicate", new CooperativeWorker());
        await using var pipeline = new EventPipeline<GuardianEvent>(8);
        using var provider = new ServiceCollection().BuildServiceProvider();

        Should.Throw<ArgumentException>(() =>
            CreateService([duplicate, duplicate], provider, new MemoryRepository(), pipeline));
    }

    [Fact]
    public async Task CooperativeWorkerStartsReportsHealthAndStops()
    {
        var worker = new CooperativeWorker();
        var registration = Registration("cooperative", worker);
        var repository = new MemoryRepository();
        await using var pipeline = new EventPipeline<GuardianEvent>(64);
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var service = CreateService([registration], provider, repository, pipeline);

        var testToken = TestContext.Current.CancellationToken;
        await service.StartAsync(testToken);
        await EventuallyAsync(() => service.Snapshots[registration.WorkerId].State is WorkerState.Running);
        (await service.CheckHealthAsync(testToken)).Status.ShouldBe(WorkerHealthStatus.Healthy);
        await service.StopAsync(testToken);

        service.Snapshots[registration.WorkerId].State.ShouldBe(WorkerState.Stopped);
        (await repository.LoadAsync(registration.WorkerId, testToken))!.State.ShouldBe(WorkerState.Stopped);
    }

    [Fact]
    public async Task FailureRestartsWorkerAndEmitsRecoveryEvents()
    {
        var worker = new FlakyWorker();
        var policy = new RestartPolicy(
            maxRestarts: 2,
            initialDelay: TimeSpan.Zero,
            maxDelay: TimeSpan.Zero,
            circuitFailureThreshold: 5);
        var registration = Registration("flaky", worker, policy);
        await using var pipeline = new EventPipeline<GuardianEvent>(64);
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var service = CreateService([registration], provider, new MemoryRepository(), pipeline);
        var seen = new ConcurrentBag<GuardianEvent>();
        using var readerCancellation = new CancellationTokenSource();
        var reader = ConsumeAsync(pipeline, seen, readerCancellation.Token);

        var testToken = TestContext.Current.CancellationToken;
        await service.StartAsync(testToken);
        await EventuallyAsync(() => worker.Invocations >= 2);
        await service.StopAsync(testToken);
        readerCancellation.Cancel();
        await IgnoreCancellationAsync(reader);

        seen.ShouldContain(item => item is WorkerFailed);
        seen.ShouldContain(item => item is WorkerRestarted);
        seen.ShouldContain(item => item is WorkerRecovered);
        seen.ShouldContain(item => item is WorkerStopped);
    }

    [Fact]
    public async Task HeartbeatTimeoutMarksUnhealthyAndExhaustsRestartPolicy()
    {
        var worker = new SilentWorker();
        var policy = new RestartPolicy(
            maxRestarts: 1,
            initialDelay: TimeSpan.Zero,
            maxDelay: TimeSpan.Zero,
            heartbeatTimeout: TimeSpan.FromMilliseconds(10),
            circuitFailureThreshold: 5);
        var registration = Registration("silent", worker, policy);
        await using var pipeline = new EventPipeline<GuardianEvent>(64);
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var service = CreateService(
            [registration],
            provider,
            new MemoryRepository(),
            pipeline,
            new GuardianOptions { MonitorInterval = TimeSpan.FromMilliseconds(2) });
        var seen = new ConcurrentBag<GuardianEvent>();
        using var readerCancellation = new CancellationTokenSource();
        var reader = ConsumeAsync(pipeline, seen, readerCancellation.Token);

        var testToken = TestContext.Current.CancellationToken;
        await service.StartAsync(testToken);
        await EventuallyAsync(() =>
            service.Snapshots.TryGetValue(registration.WorkerId, out var snapshot) &&
            snapshot.State is WorkerState.Failed);
        await service.StopAsync(testToken);
        readerCancellation.Cancel();
        await IgnoreCancellationAsync(reader);

        seen.ShouldContain(item => item is WorkerUnhealthy);
        worker.Invocations.ShouldBe(2);
    }

    [Fact]
    public async Task SemaphoreBoundsParallelWorkerExecution()
    {
        var tracker = new ConcurrencyTracker();
        var registrations = Enumerable.Range(0, 8)
            .Select(index => Registration($"worker-{index}", new OneShotWorker(tracker)))
            .ToArray();
        await using var pipeline = new EventPipeline<GuardianEvent>(128);
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var service = CreateService(
            registrations,
            provider,
            new MemoryRepository(),
            pipeline,
            new GuardianOptions { MaxConcurrency = 2, MonitorInterval = TimeSpan.FromMilliseconds(2) });

        var testToken = TestContext.Current.CancellationToken;
        await service.StartAsync(testToken);
        await EventuallyAsync(() => service.Snapshots.Count == 8 &&
            service.Snapshots.Values.All(snapshot => snapshot.State is WorkerState.Stopped));
        tracker.Maximum.ShouldBeInRange(1, 2);
        await service.StopAsync(testToken);
    }

    [Fact]
    public async Task HealthAggregationHandlesDegradedAndThrownProbes()
    {
        var degraded = Registration("degraded", new ProbeWorker(WorkerHealthStatus.Degraded));
        var broken = Registration("broken", new BrokenProbeWorker());
        await using var pipeline = new EventPipeline<GuardianEvent>(8);
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var degradedService = CreateService([degraded], provider, new MemoryRepository(), pipeline);
        var testToken = TestContext.Current.CancellationToken;
        (await degradedService.CheckHealthAsync(testToken)).Status.ShouldBe(WorkerHealthStatus.Degraded);

        await using var brokenPipeline = new EventPipeline<GuardianEvent>(8);
        using var brokenService = CreateService([broken], provider, new MemoryRepository(), brokenPipeline);
        (await brokenService.CheckHealthAsync(testToken)).Status.ShouldBe(WorkerHealthStatus.Unhealthy);
    }

    [Fact]
    public async Task DependencyInjectionRegistersHostedServiceAndDefaultRepository()
    {
        var database = Path.Combine(Path.GetTempPath(), $"guardian-host-{Guid.NewGuid():N}.db");
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services
                .AddWorkerGuardian(options =>
                {
                    options.DatabasePath = database;
                    options.MonitorInterval = TimeSpan.FromMilliseconds(5);
                })
                .AddGuardianWorker<CooperativeWorker>(new WorkerId("hosted"));
            using var host = builder.Build();
            await host.StartAsync(TestContext.Current.CancellationToken);
            var guardian = host.Services.GetRequiredService<IWorkerGuardian>();
            await EventuallyAsync(() => guardian.Snapshots.TryGetValue(new WorkerId("hosted"), out var snapshot) &&
                snapshot.State is WorkerState.Running);
            await host.StopAsync(TestContext.Current.CancellationToken);
            guardian.Snapshots[new WorkerId("hosted")].State.ShouldBe(WorkerState.Stopped);
        }
        finally
        {
            DeleteSqliteFiles(database);
        }
    }

    private static WorkerRegistration Registration(
        string id,
        IGuardianWorker worker,
        RestartPolicy? policy = null) =>
        new(new WorkerId(id), _ => worker, policy ?? new RestartPolicy());

    private static WorkerGuardianService CreateService(
        IEnumerable<WorkerRegistration> registrations,
        IServiceProvider provider,
        IWorkerStateRepository repository,
        EventPipeline<GuardianEvent> pipeline,
        GuardianOptions? options = null) =>
        new(
            registrations,
            provider,
            repository,
            pipeline,
            new InMemoryGuardianMetrics(),
            Options.Create(options ?? new GuardianOptions()),
            TimeProvider.System,
            NullLogger<WorkerGuardianService>.Instance);

    private static async Task ConsumeAsync(
        EventPipeline<GuardianEvent> pipeline,
        ConcurrentBag<GuardianEvent> destination,
        CancellationToken cancellationToken)
    {
        await foreach (var item in pipeline.ReadAllAsync(cancellationToken))
        {
            destination.Add(item);
        }
    }

    private static async Task EventuallyAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(3));
        while (!condition())
        {
            await Task.Delay(5, cancellation.Token);
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void DeleteSqliteFiles(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
        {
            if (File.Exists(path + suffix))
            {
                File.Delete(path + suffix);
            }
        }
    }

    public sealed class CooperativeWorker : IGuardianWorker
    {
        public async ValueTask ExecuteAsync(WorkerExecutionContext context, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                context.Heartbeat();
                await Task.Delay(5, cancellationToken);
            }
        }

        public ValueTask<WorkerHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new WorkerHealth(WorkerHealthStatus.Healthy));
    }

    private sealed class FlakyWorker : IGuardianWorker
    {
        public int Invocations { get; private set; }

        public async ValueTask ExecuteAsync(WorkerExecutionContext context, CancellationToken cancellationToken)
        {
            Invocations++;
            if (Invocations == 1)
            {
                throw new IOException("Injected transient failure.");
            }

            await new CooperativeWorker().ExecuteAsync(context, cancellationToken);
        }

        public ValueTask<WorkerHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new WorkerHealth(WorkerHealthStatus.Healthy));
    }

    private sealed class SilentWorker : IGuardianWorker
    {
        public int Invocations { get; private set; }

        public async ValueTask ExecuteAsync(WorkerExecutionContext context, CancellationToken cancellationToken)
        {
            Invocations++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask<WorkerHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new WorkerHealth(WorkerHealthStatus.Healthy));
    }

    private sealed class OneShotWorker(ConcurrencyTracker tracker) : IGuardianWorker
    {
        public async ValueTask ExecuteAsync(WorkerExecutionContext context, CancellationToken cancellationToken)
        {
            tracker.Enter();
            try
            {
                context.Heartbeat();
                await Task.Delay(20, cancellationToken);
            }
            finally
            {
                tracker.Exit();
            }
        }

        public ValueTask<WorkerHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new WorkerHealth(WorkerHealthStatus.Healthy));
    }

    private sealed class ProbeWorker(WorkerHealthStatus status) : IGuardianWorker
    {
        public ValueTask ExecuteAsync(WorkerExecutionContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<WorkerHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new WorkerHealth(status));
    }

    private sealed class BrokenProbeWorker : IGuardianWorker
    {
        public ValueTask ExecuteAsync(WorkerExecutionContext context, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<WorkerHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Probe failed.");
    }

    private sealed class ConcurrencyTracker
    {
        private int active;
        private int maximum;

        public int Maximum => maximum;

        public void Enter()
        {
            var current = Interlocked.Increment(ref active);
            int observed;
            do
            {
                observed = maximum;
            }
            while (current > observed && Interlocked.CompareExchange(ref maximum, current, observed) != observed);
        }

        public void Exit() => Interlocked.Decrement(ref active);
    }

    private sealed class MemoryRepository : IWorkerStateRepository
    {
        private readonly ConcurrentDictionary<WorkerId, WorkerSnapshot> values = new();

        public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SaveAsync(WorkerSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            values[snapshot.WorkerId] = snapshot;
            return ValueTask.CompletedTask;
        }

        public ValueTask<WorkerSnapshot?> LoadAsync(
            WorkerId workerId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(values.GetValueOrDefault(workerId));

        public ValueTask<IReadOnlyList<WorkerSnapshot>> LoadAllAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<WorkerSnapshot>>(values.Values.ToArray());
    }
}

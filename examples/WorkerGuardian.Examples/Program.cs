using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkerGuardian;

var scenario = args.FirstOrDefault()?.ToLowerInvariant() ?? "basic";
if (scenario is not ("basic" or "retry" or "concurrency"))
{
    Console.Error.WriteLine("Usage: dotnet run --project examples/WorkerGuardian.Examples -- [basic|retry|concurrency]");
    return 2;
}

var database = Path.Combine(Path.GetTempPath(), $"worker-guardian-{Guid.NewGuid():N}.db");
try
{
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);
    builder.Services.AddWorkerGuardian(options =>
    {
        options.DatabasePath = database;
        options.MaxConcurrency = scenario == "concurrency" ? 2 : 4;
        options.MonitorInterval = TimeSpan.FromMilliseconds(5);
    });

    var tracker = new ConcurrencyTracker();
    switch (scenario)
    {
        case "basic":
            Register(builder.Services, "basic-worker", new DemoWorker(tracker));
            break;
        case "retry":
            Register(
                builder.Services,
                "flaky-worker",
                new DemoWorker(tracker, failFirstAttempt: true),
                new RestartPolicy(maxRestarts: 2, initialDelay: TimeSpan.FromMilliseconds(10)));
            break;
        case "concurrency":
            foreach (var index in Enumerable.Range(1, 6))
            {
                Register(builder.Services, $"parallel-{index}", new DemoWorker(tracker));
            }

            break;
    }

    using var host = builder.Build();
    await host.StartAsync();
    var guardian = host.Services.GetRequiredService<IWorkerGuardian>();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (guardian.Snapshots.Count == 0 || guardian.Snapshots.Values.Any(item => item.State is not WorkerState.Stopped))
    {
        await Task.Delay(5, timeout.Token);
    }

    await host.StopAsync();
    Console.WriteLine($"scenario={scenario} workers={guardian.Snapshots.Count} max_parallel={tracker.Maximum}");
    foreach (var snapshot in guardian.Snapshots.Values.OrderBy(item => item.WorkerId))
    {
        Console.WriteLine($"worker={snapshot.WorkerId} state={snapshot.State} attempt={snapshot.Attempt}");
    }

    return 0;
}
finally
{
    foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
    {
        File.Delete(database + suffix);
    }
}

static void Register(
    IServiceCollection services,
    string id,
    IGuardianWorker worker,
    RestartPolicy? policy = null) =>
    services.AddSingleton(new WorkerRegistration(
        new WorkerId(id),
        _ => worker,
        policy ?? new RestartPolicy()));

internal sealed class DemoWorker(ConcurrencyTracker tracker, bool failFirstAttempt = false) : IGuardianWorker
{
    private int invocations;

    public async ValueTask ExecuteAsync(WorkerExecutionContext context, CancellationToken cancellationToken)
    {
        if (failFirstAttempt && Interlocked.Increment(ref invocations) == 1)
        {
            throw new IOException("Injected transient failure for the retry example.");
        }

        tracker.Enter();
        try
        {
            context.Heartbeat();
            await Task.Delay(25, cancellationToken);
        }
        finally
        {
            tracker.Exit();
        }
    }

    public ValueTask<WorkerHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new WorkerHealth(WorkerHealthStatus.Healthy));
}

internal sealed class ConcurrencyTracker
{
    private int active;
    private int maximum;

    public int Maximum => Volatile.Read(ref maximum);

    public void Enter()
    {
        var current = Interlocked.Increment(ref active);
        int observed;
        do
        {
            observed = Volatile.Read(ref maximum);
        }
        while (current > observed && Interlocked.CompareExchange(ref maximum, current, observed) != observed);
    }

    public void Exit() => Interlocked.Decrement(ref active);
}

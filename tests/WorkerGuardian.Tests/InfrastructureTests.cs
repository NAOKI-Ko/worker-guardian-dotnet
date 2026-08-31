using Microsoft.Extensions.Options;
using Shouldly;

namespace WorkerGuardian.Tests;

public sealed class InfrastructureTests : IAsyncLifetime
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"guardian-{Guid.NewGuid():N}.db");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task EventPipelineAppliesBackpressureAndCompletes()
    {
        var testToken = TestContext.Current.CancellationToken;
        await using var pipeline = new EventPipeline<GuardianEvent>(1);
        var item = new WorkerStarted(new WorkerId("worker"), 1, DateTimeOffset.UtcNow);
        await pipeline.PublishAsync(item, testToken);
        await using var reader = pipeline.ReadAllAsync(testToken).GetAsyncEnumerator(testToken);
        (await reader.MoveNextAsync()).ShouldBeTrue();
        reader.Current.ShouldBe(item);
        await pipeline.DisposeAsync();
        (await reader.MoveNextAsync()).ShouldBeFalse();
        await Should.ThrowAsync<ObjectDisposedException>(async () => await pipeline.PublishAsync(item, testToken));
        Should.Throw<ArgumentOutOfRangeException>(() => new EventPipeline<GuardianEvent>(0));
    }

    [Fact]
    public async Task SqliteRepositoryRoundTripsSnapshots()
    {
        var testToken = TestContext.Current.CancellationToken;
        await using var repository = CreateRepository();
        await repository.InitializeAsync(testToken);
        var now = DateTimeOffset.UtcNow;
        var snapshot = WorkerSnapshot.Registered(new WorkerId("worker"), now)
            .Transition(WorkerState.Starting, now) with
        {
            Attempt = 2,
            LastHeartbeat = now,
        };
        await repository.SaveAsync(snapshot, testToken);
        (await repository.LoadAsync(snapshot.WorkerId, testToken)).ShouldBe(snapshot);
        (await repository.LoadAsync(new WorkerId("missing"), testToken)).ShouldBeNull();
        (await repository.LoadAllAsync(testToken)).ShouldBe([snapshot]);
    }

    [Fact]
    public void InMemoryMetricsCollectCountersAndMeasurements()
    {
        var metrics = new InMemoryGuardianMetrics();
        var id = new WorkerId("worker");
        metrics.Count("runs", id).ShouldBe(0);
        metrics.Increment("runs", id);
        metrics.Record("duration", 1.5, id);
        metrics.Count("runs", id).ShouldBe(1);
        metrics.Values("duration", id).ShouldBe([1.5]);
        metrics.Values("missing", id).ShouldBeEmpty();
    }

    [Fact]
    public void FailureInformationUsesImmutableMetadata()
    {
        var info = FailureInfo.From(new IOException("disk"));
        info.ShouldBe(new FailureInfo(nameof(IOException), "disk"));
        GuardianEvent value = new WorkerUnhealthy(new WorkerId("worker"), "timeout", DateTimeOffset.UnixEpoch);
        (value switch
        {
            WorkerUnhealthy unhealthy => unhealthy.Reason,
            _ => string.Empty,
        }).ShouldBe("timeout");
    }

    private SqliteWorkerStateRepository CreateRepository() => new(
        Options.Create(new GuardianOptions { DatabasePath = databasePath }));
}

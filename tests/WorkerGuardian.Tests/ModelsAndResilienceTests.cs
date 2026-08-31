using FsCheck.Fluent;
using Shouldly;

namespace WorkerGuardian.Tests;

public sealed class ModelsAndResilienceTests
{
    [Fact]
    public void WorkerIdentifiersAreValidatedAndOrdered()
    {
        var first = new WorkerId("a");
        var second = new WorkerId("b");
        first.CompareTo(second).ShouldBeLessThan(0);
        first.ToString().ShouldBe("a");
        Should.Throw<ArgumentException>(() => new WorkerId(" "));
    }

    [Fact]
    public void WorkerSnapshotIsImmutableAndRejectsInvalidTransitions()
    {
        var now = DateTimeOffset.UtcNow;
        var registered = WorkerSnapshot.Registered(new WorkerId("worker"), now);
        var running = registered
            .Transition(WorkerState.Starting, now)
            .Transition(WorkerState.Running, now);
        registered.State.ShouldBe(WorkerState.Registered);
        running.State.ShouldBe(WorkerState.Running);
        Should.Throw<InvalidOperationException>(() => registered.Transition(WorkerState.Running, now));
    }

    [Fact]
    public void BackoffIsBoundedForGeneratedInputs()
    {
        var policy = new RestartPolicy(
            initialDelay: TimeSpan.FromMilliseconds(10),
            maxDelay: TimeSpan.FromSeconds(2),
            multiplier: 2,
            jitter: 0.5);
        var generatedInputs = Gen.Choose(1, 100).Zip(Gen.Choose(0, 1000)).Sample(100);
        foreach (var (attempt, random) in generatedInputs)
        {
            var delay = policy.DelayFor(attempt, random / 1000d);
            delay.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
            delay.ShouldBeLessThanOrEqualTo(policy.MaxDelay);
        }
    }

    [Theory]
    [InlineData(-1, 1, 0.1)]
    [InlineData(1, 0.5, 0.1)]
    [InlineData(1, 1, 1.1)]
    public void RestartPolicyRejectsInvalidValues(int restarts, double multiplier, double jitter)
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new RestartPolicy(maxRestarts: restarts, multiplier: multiplier, jitter: jitter));
    }

    [Fact]
    public void RestartPolicyRejectsInvalidTimeoutsAndThresholds()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new RestartPolicy(heartbeatTimeout: TimeSpan.Zero));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new RestartPolicy(circuitFailureThreshold: 0));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new RestartPolicy(circuitRecoveryTimeout: TimeSpan.FromTicks(-1)));

        var recovery = TimeSpan.FromMilliseconds(250);
        new RestartPolicy(circuitRecoveryTimeout: recovery).CircuitRecoveryTimeout.ShouldBe(recovery);
    }

    [Fact]
    public async Task CircuitBreakerOpensAndRecovers()
    {
        var time = new ManualTimeProvider();
        var breaker = new CircuitBreaker(2, TimeSpan.FromSeconds(1), time);
        var testToken = TestContext.Current.CancellationToken;
        for (var index = 0; index < 2; index++)
        {
            await Should.ThrowAsync<InvalidOperationException>(async () =>
                await breaker.ExecuteAsync<int>(_ => throw new InvalidOperationException("transient"), testToken));
        }

        breaker.State.ShouldBe(CircuitState.Open);
        await Should.ThrowAsync<CircuitOpenException>(async () =>
            await breaker.ExecuteAsync(_ => ValueTask.FromResult(1), testToken));
        time.Advance(TimeSpan.FromSeconds(1));
        breaker.State.ShouldBe(CircuitState.HalfOpen);
        (await breaker.ExecuteAsync(_ => ValueTask.FromResult(42), testToken)).ShouldBe(42);
        breaker.State.ShouldBe(CircuitState.Closed);
        Should.Throw<ArgumentOutOfRangeException>(() => new CircuitBreaker(0, TimeSpan.Zero));
    }

    [Fact]
    public async Task CancellationDoesNotCountAsCircuitFailure()
    {
        var breaker = new CircuitBreaker(1, TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await breaker.ExecuteAsync<int>(_ => throw new OperationCanceledException(cancellation.Token), cancellation.Token));
        breaker.State.ShouldBe(CircuitState.Closed);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }
}

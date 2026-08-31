using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

public static class WorkerGuardianServiceCollectionExtensions
{
    public static IServiceCollection AddWorkerGuardian(
        this IServiceCollection services,
        Action<WorkerGuardian.GuardianOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<WorkerGuardian.GuardianOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.Validate(static value => value.IsValid(), "Worker Guardian options are invalid.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<WorkerGuardian.IGuardianMetrics, WorkerGuardian.NullGuardianMetrics>();
        services.TryAddSingleton<WorkerGuardian.IWorkerStateRepository, WorkerGuardian.SqliteWorkerStateRepository>();
        services.TryAddSingleton(provider => new WorkerGuardian.EventPipeline<WorkerGuardian.GuardianEvent>(
            provider.GetRequiredService<IOptions<WorkerGuardian.GuardianOptions>>().Value.EventCapacity));
        services.TryAddSingleton<WorkerGuardian.WorkerGuardianService>();
        services.TryAddSingleton<WorkerGuardian.IWorkerGuardian>(
            provider => provider.GetRequiredService<WorkerGuardian.WorkerGuardianService>());
        services.AddHostedService(
            provider => provider.GetRequiredService<WorkerGuardian.WorkerGuardianService>());
        return services;
    }

    public static IServiceCollection AddGuardianWorker<TWorker>(
        this IServiceCollection services,
        WorkerGuardian.WorkerId workerId,
        WorkerGuardian.RestartPolicy? restartPolicy = null)
        where TWorker : class, WorkerGuardian.IGuardianWorker
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<TWorker>();
        services.AddSingleton(new WorkerGuardian.WorkerRegistration(
            workerId,
            static provider => provider.GetRequiredService<TWorker>(),
            restartPolicy ?? new WorkerGuardian.RestartPolicy()));
        return services;
    }
}


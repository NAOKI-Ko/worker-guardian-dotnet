using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace WorkerGuardian;

public interface IWorkerStateRepository
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(WorkerSnapshot snapshot, CancellationToken cancellationToken = default);

    ValueTask<WorkerSnapshot?> LoadAsync(WorkerId workerId, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<WorkerSnapshot>> LoadAllAsync(CancellationToken cancellationToken = default);
}

public sealed class SqliteWorkerStateRepository : IWorkerStateRepository, IAsyncDisposable
{
    private readonly string connectionString;
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public SqliteWorkerStateRepository(IOptions<GuardianOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.Value.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS worker_snapshots (
                worker_id TEXT PRIMARY KEY,
                state INTEGER NOT NULL,
                attempt INTEGER NOT NULL,
                last_heartbeat TEXT NULL,
                detail TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SaveAsync(WorkerSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO worker_snapshots(worker_id, state, attempt, last_heartbeat, detail, updated_at)
                VALUES ($id, $state, $attempt, $heartbeat, $detail, $updated)
                ON CONFLICT(worker_id) DO UPDATE SET
                    state = excluded.state,
                    attempt = excluded.attempt,
                    last_heartbeat = excluded.last_heartbeat,
                    detail = excluded.detail,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$id", snapshot.WorkerId.Value);
            command.Parameters.AddWithValue("$state", (int)snapshot.State);
            command.Parameters.AddWithValue("$attempt", snapshot.Attempt);
            command.Parameters.AddWithValue(
                "$heartbeat",
                snapshot.LastHeartbeat is { } heartbeat ? heartbeat.ToString("O") : DBNull.Value);
            command.Parameters.AddWithValue("$detail", snapshot.Detail);
            command.Parameters.AddWithValue("$updated", snapshot.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async ValueTask<WorkerSnapshot?> LoadAsync(
        WorkerId workerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM worker_snapshots WHERE worker_id = $id";
        command.Parameters.AddWithValue("$id", workerId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSnapshot(reader) : null;
    }

    public async ValueTask<IReadOnlyList<WorkerSnapshot>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM worker_snapshots ORDER BY worker_id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = new List<WorkerSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            snapshots.Add(ReadSnapshot(reader));
        }

        return snapshots;
    }

    public ValueTask DisposeAsync()
    {
        writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private static WorkerSnapshot ReadSnapshot(SqliteDataReader reader) => new(
        new WorkerId(reader.GetString(reader.GetOrdinal("worker_id"))),
        (WorkerState)reader.GetInt32(reader.GetOrdinal("state")),
        reader.GetInt32(reader.GetOrdinal("attempt")),
        reader.IsDBNull(reader.GetOrdinal("last_heartbeat"))
            ? null
            : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("last_heartbeat")), null),
        reader.GetString(reader.GetOrdinal("detail")),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at")), null));
}


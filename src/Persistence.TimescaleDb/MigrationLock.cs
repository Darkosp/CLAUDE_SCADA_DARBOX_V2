using Npgsql;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>
/// One migrator at a time on a database (ADR-0014): a session-level advisory lock, held on a
/// connection of its own for the whole of a migrator run.
/// </summary>
/// <remarks>
/// DbUp reads its journal and then executes, with no lock of its own, so two runs meeting on
/// an existing database both apply a new script — every run reporting success. The lock is in
/// the database rather than in Compose because the database is the one thing every way of
/// starting a second run shares. Advisory locks belong to one database, so migrators working
/// on different databases never wait for each other.
/// </remarks>
internal sealed class MigrationLock : IAsyncDisposable
{
    /// <summary>
    /// The lock's key: "SCADA_MG" in ASCII. PostgreSQL shows a bigint advisory key in
    /// <c>pg_locks</c> split into <c>classid</c> (high half) and <c>objid</c> (low half) with
    /// <c>objsubid = 1</c> — that is how an operator finds who holds it.
    /// </summary>
    internal const long Key = 0x5343_4144_415F_4D47;

    /// <summary>What the lock's own session calls itself in <c>pg_stat_activity</c>.</summary>
    internal const string ApplicationName = "scada-migrator";

    private const string LockNotAvailable = "55P03";

    private readonly NpgsqlConnection _connection;

    private MigrationLock(NpgsqlConnection connection) => _connection = connection;

    /// <summary>The server process holding the lock, as <c>pg_stat_activity</c> shows it.</summary>
    internal int ProcessId => _connection.ProcessID;

    /// <summary>Waits up to <paramref name="timeout"/> for the lock on the database the connection string names.</summary>
    /// <exception cref="MigrationLockTimeoutException">Another run held the lock for the whole wait.</exception>
    public static async Task<MigrationLock> AcquireAsync(
        string connectionString,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        // Unpooled: a pooled connection goes back to the pool on close with its session — and
        // so its lock — still alive. Closing this one ends the session, so the lock cannot
        // outlive the run, and a process that dies releases it without anyone cleaning up.
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            ApplicationName = ApplicationName,
        };

        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // The server bounds the wait. SET takes no parameters; the value is an integer.
            var milliseconds = (long)Math.Ceiling(timeout.TotalMilliseconds);
            await using (var bound = new NpgsqlCommand($"SET lock_timeout = {milliseconds}", connection))
            {
                await bound.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // The client's own timeout must not cut the wait short of the server's.
            await using var take = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection)
            {
                CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds) + 30,
            };
            take.Parameters.AddWithValue("key", Key);

            try
            {
                await take.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception) when (exception.SqlState == LockNotAvailable)
            {
                var holder = await DescribeHolderAsync(connection, cancellationToken).ConfigureAwait(false);
                throw new MigrationLockTimeoutException(connection.Database, timeout, holder, exception);
            }

            return new MigrationLock(connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", _connection);
            release.Parameters.AddWithValue("key", Key);
            await release.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // A broken connection has already ended the session, and with it the lock.
        }
        finally
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Who holds the lock now, for the refusal message; null if it was let go meanwhile.</summary>
    private static async Task<string?> DescribeHolderAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var query = new NpgsqlCommand(
            """
            SELECT a.pid, coalesce(nullif(a.application_name, ''), 'unnamed'), a.backend_start
            FROM pg_locks l
            JOIN pg_stat_activity a ON a.pid = l.pid
            WHERE l.locktype = 'advisory'
              AND l.granted
              AND l.database = (SELECT oid FROM pg_database WHERE datname = current_database())
              AND l.classid = @high::oid AND l.objid = @low::oid AND l.objsubid = 1
            """,
            connection);
        query.Parameters.AddWithValue("high", (long)(Key >> 32));
        query.Parameters.AddWithValue("low", Key & 0xFFFF_FFFF);

        await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return $"process {reader.GetInt32(0)} ('{reader.GetString(1)}', connected since {reader.GetDateTime(2):u})";
    }
}

/// <summary>Another migrator held the database for longer than this one was allowed to wait.</summary>
/// <remarks>Names the database and the holder, never the connection string: that carries the privileged password.</remarks>
public sealed class MigrationLockTimeoutException(
    string database,
    TimeSpan waited,
    string? holder,
    Exception inner) : Exception(
    $"Another migrator holds database '{database}': waited {waited} for the migration lock and gave up. " +
    "Nothing was applied. " +
    (holder is null ? "It had let go by the time this was reported; run the migrator again. " : $"It is held by {holder}. ") +
    "Wait for that run to finish; if it is hung, ending that process releases the lock (ADR-0014).",
    inner);

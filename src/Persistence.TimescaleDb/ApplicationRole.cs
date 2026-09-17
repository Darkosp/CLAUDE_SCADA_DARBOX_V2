using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>
/// The unprivileged database role the Gateway connects as (ADR-0011, ADR-0012).
/// </summary>
/// <remarks>
/// Migration 0008 creates the role and its grants but no password, because a migration
/// script is committed and reviewable by design. The password arrives here, from the
/// migrator's environment.
/// </remarks>
public static class ApplicationRole
{
    public const string Name = "scada_app";

    private const string InternalError = "XX000";
    private const string InvalidPassword = "28P01";
    private const string LoginNotPermitted = "28000";
    private const int MaxAttempts = 10;
    private const int ScramIterations = 4096;

    /// <summary>
    /// Allows the application role to log in with <paramref name="password"/>, then proves
    /// it can.
    /// </summary>
    /// <remarks>
    /// The password is sent as a SCRAM-SHA-256 verifier computed here rather than as plain
    /// text, so it cannot surface in the server's statement log even with statement logging
    /// switched on.
    /// </remarks>
    public static async Task SetPasswordAsync(
        string privilegedConnectionString,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("The application role's password must not be empty.", nameof(password));
        }

        // Already able to log in with this password: leave the role alone. A rerun of the
        // migrator then writes nothing to the cluster's shared catalogue at all, instead of
        // rewriting an identical password under a fresh salt.
        if (await CanLogInAsync(privilegedConnectionString, password, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // The verifier is base64 and fixed punctuation, so it cannot close the literal.
        var statement = $"ALTER ROLE {Name} WITH LOGIN PASSWORD '{ScramVerifier(password)}'";

        await using (var dataSource = NpgsqlDataSource.Create(privilegedConnectionString))
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var command = dataSource.CreateCommand(statement);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (PostgresException exception) when (attempt < MaxAttempts && exception.SqlState == InternalError)
                {
                    // A role belongs to the whole cluster, so two migrators finishing at the
                    // same moment against different databases can collide on the same
                    // catalogue row ("tuple concurrently updated"). Both want the same
                    // outcome, so try again — after a random pause: callers that collided
                    // once and all waited the same fixed time would simply collide again.
                    var pause = TimeSpan.FromMilliseconds(Random.Shared.Next(25, 250));
                    await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (!await CanLogInAsync(privilegedConnectionString, password, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"The password was set, but the application role '{Name}' still cannot log in with it.");
        }
    }

    /// <summary>Whether the application role can log in with <paramref name="password"/> right now.</summary>
    private static async Task<bool> CanLogInAsync(
        string privilegedConnectionString,
        string password,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var check = NpgsqlDataSource.Create(ConnectionStringFor(privilegedConnectionString, password));
            await using var probe = check.CreateCommand("SELECT 1");
            await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (PostgresException exception) when (exception.SqlState is InvalidPassword or LoginNotPermitted)
        {
            return false;
        }
    }

    /// <summary>
    /// The application's connection string: the same server and database as
    /// <paramref name="connectionString"/>, as the application role.
    /// </summary>
    public static string ConnectionStringFor(string connectionString, string password) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        {
            Username = Name,
            Password = password,
        }.ConnectionString;

    /// <summary>
    /// A PostgreSQL SCRAM-SHA-256 password verifier (RFC 5802, RFC 7677), in the form the
    /// server stores and accepts in place of a password.
    /// </summary>
    private static string ScramVerifier(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);

        // Normalised the way the client normalises before its half of the exchange, so a
        // password with composed characters derives the same key on both sides.
        var passwordBytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
        var saltedPassword = Rfc2898DeriveBytes.Pbkdf2(
            passwordBytes, salt, ScramIterations, HashAlgorithmName.SHA256, 32);

        var clientKey = HMACSHA256.HashData(saltedPassword, "Client Key"u8);
        var storedKey = SHA256.HashData(clientKey);
        var serverKey = HMACSHA256.HashData(saltedPassword, "Server Key"u8);

        return $"SCRAM-SHA-256${ScramIterations}:{Convert.ToBase64String(salt)}" +
               $"${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}";
    }
}

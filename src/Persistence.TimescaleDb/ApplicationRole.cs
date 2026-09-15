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
                catch (PostgresException exception) when (attempt < 5 && exception.SqlState == InternalError)
                {
                    // A role belongs to the whole cluster, so two migrators finishing at the
                    // same moment against different databases can collide on the same
                    // catalogue row ("tuple concurrently updated"). Both want the same
                    // outcome; try again.
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await using var check = NpgsqlDataSource.Create(ConnectionStringFor(privilegedConnectionString, password));
        await using var probe = check.CreateCommand("SELECT 1");
        await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
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

using ScadaDarbox.Core.Security;

namespace ScadaDarbox.Gateway.Security;

/// <summary>
/// Creates the first Admin from an explicitly supplied name and password (ADR-0011).
/// </summary>
/// <remarks>
/// No default credentials ship — shipped defaults are a known, recurring weakness in SCADA
/// deployments. The values are read from the command line or the environment and nowhere
/// else: in particular not from appsettings.json, which is a committed file and would make
/// whatever it contained a default.
/// </remarks>
internal static class InitialAdmin
{
    public const string UsernameVariable = "SCADA_INITIAL_ADMIN_USERNAME";
    public const string PasswordVariable = "SCADA_INITIAL_ADMIN_PASSWORD";
    public const string UsernameOption = "--initial-admin-username";
    public const string PasswordOption = "--initial-admin-password";

    public enum Outcome
    {
        NotRequested,
        Created,
        IgnoredBecauseUsersExist,
    }

    public static async Task<Outcome> EnsureAsync(
        string[] args,
        ISecurityStore store,
        IAuditLog audit,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var username = Option(args, UsernameOption) ?? Environment.GetEnvironmentVariable(UsernameVariable);
        var password = Option(args, PasswordOption) ?? Environment.GetEnvironmentVariable(PasswordVariable);

        if (string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password))
        {
            return Outcome.NotRequested;
        }

        if ((Credentials.ProblemWithUsername(username) ?? Credentials.ProblemWithPassword(password)) is { } problem)
        {
            throw new InvalidOperationException($"The initial Admin cannot be created: {problem}");
        }

        var user = new NewUser(Guid.NewGuid(), tenantId, username!.Trim(), Authenticator.Hash(password!), IsAdmin: true);

        if (!await store.CreateFirstUserAsync(user, cancellationToken).ConfigureAwait(false))
        {
            return Outcome.IgnoredBecauseUsersExist;
        }

        await audit.AppendAsync(
            new AuditEntry(
                ActorUserId: null,
                "user.create_initial_admin",
                "user",
                user.Id,
                new Dictionary<string, object?> { ["username"] = user.Username }),
            CancellationToken.None).ConfigureAwait(false);

        return Outcome.Created;
    }

    private static string? Option(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith(name + "=", StringComparison.Ordinal))
            {
                return args[index][(name.Length + 1)..];
            }

            if (args[index] == name && index + 1 < args.Length)
            {
                return args[index + 1];
            }
        }

        return null;
    }
}

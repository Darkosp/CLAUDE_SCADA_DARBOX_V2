using ScadaDarbox.Persistence.TimescaleDb;
using Xunit;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// When the migrator may leave the application role's password alone.
/// </summary>
/// <remarks>
/// Skipping the password step on a rerun relies on "the role can already log in with it".
/// That only means something if the server checks passwords: under a trust rule any password
/// logs in, and trusting that login would leave the real password never set — invisible until
/// the rule is tightened, and then the Gateway cannot connect.
/// </remarks>
public sealed class ApplicationRoleTests
{
    private const string Password = "the-password-the-migrator-was-given";

    [Fact]
    public async Task A_login_on_a_server_that_checks_passwords_is_trusted()
    {
        var probed = new List<string>();

        var inForce = await ApplicationRole.PasswordAlreadyInForceAsync(
            candidate =>
            {
                probed.Add(candidate);
                return Task.FromResult(candidate == Password);
            },
            Password);

        Assert.True(inForce);

        // The second probe really was a different password — a "decoy" equal to the password
        // would make any server look like it checks passwords.
        Assert.Equal(2, probed.Count);
        Assert.NotEqual(Password, probed[1]);
    }

    [Fact]
    public async Task A_server_that_accepts_any_password_is_not_trusted()
    {
        var inForce = await ApplicationRole.PasswordAlreadyInForceAsync(_ => Task.FromResult(true), Password);

        Assert.False(inForce);
    }

    [Fact]
    public async Task A_password_that_does_not_log_in_is_not_in_force()
    {
        var inForce = await ApplicationRole.PasswordAlreadyInForceAsync(_ => Task.FromResult(false), Password);

        Assert.False(inForce);
    }
}

/// <summary>The same decision against the real server, which checks passwords.</summary>
public sealed class ApplicationRoleDatabaseTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _database;

    public ApplicationRoleDatabaseTests(TestDatabase database) => _database = database;

    [RequiresDatabaseFact]
    public async Task On_the_real_server_the_current_password_is_in_force_and_a_wrong_one_is_refused()
    {
        Task<bool> CanLogIn(string candidate) =>
            ApplicationRole.CanLogInAsync(_database.PrivilegedConnectionString, candidate, CancellationToken.None);

        // This server refuses a wrong password — so every other test run here, where the
        // migrator skips the password step, is taking that skip for a real reason.
        Assert.False(await CanLogIn($"wrong-{Guid.NewGuid():N}"));

        Assert.True(await ApplicationRole.PasswordAlreadyInForceAsync(CanLogIn, TestDatabase.ApplicationPassword));
        Assert.False(await ApplicationRole.PasswordAlreadyInForceAsync(CanLogIn, $"not-set-{Guid.NewGuid():N}"));
    }
}

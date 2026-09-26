using Xunit;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// The fixture's own lifecycle, exercised with no database on purpose: the state covered here
/// exists only when no server answered.
/// </summary>
/// <remarks>
/// A class fixture whose every test skips is still disposed. Without the guard at the top of
/// <see cref="TestDatabase.DisposeAsync"/>, that made a run with no database reachable exit 1
/// while every project printed <c>Passed!</c> — measured on 2026-09-26 as
/// <c>[Test Class Cleanup Failure (…)] System.NullReferenceException</c> on all eleven test
/// classes that take the fixture. This test builds the fixture and disposes it without ever
/// initializing it, which is exactly what the skip path leaves behind.
/// </remarks>
public sealed class TestDatabaseTests
{
    /// <summary>
    /// Disposing a fixture that never created anything must be a no-op. Not throwing is the
    /// whole claim; there is nothing to assert beyond it.
    /// </summary>
    [Fact]
    public async Task A_fixture_that_never_initialized_disposes_cleanly()
    {
        var database = new TestDatabase();

        await database.DisposeAsync();
    }
}

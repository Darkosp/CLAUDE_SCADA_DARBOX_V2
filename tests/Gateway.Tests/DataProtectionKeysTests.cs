using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Gateway.Tests.Hosting;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// Found walking the Phase 6 gate: ASP.NET Core creates a Data Protection key ring at startup
/// whether or not anything uses it, and inside a container it lived in the container — lost
/// with it, and a warning in the deployment log on every start.
/// </summary>
public sealed class DataProtectionKeysTests
{
    [RequiresDatabaseFact]
    public async Task A_configured_keys_directory_gets_the_key_ring_once_and_keeps_it()
    {
        await using var database = await ScratchDatabase.CreateMigratedAsync();
        var keys = Path.Combine(Path.GetTempPath(), $"scada-keys-{Guid.NewGuid():N}");

        try
        {
            await StartAndStopAsync(database, keys);
            var first = KeyFiles(keys);

            // The ring was written where the deployment asked, not into the process's home.
            Assert.NotEmpty(first);

            // And a restart — a new container on the same volume — reuses it rather than
            // making another, which is what keeps the start quiet from then on.
            await StartAndStopAsync(database, keys);
            Assert.Equal(first, KeyFiles(keys));
        }
        finally
        {
            if (Directory.Exists(keys))
            {
                Directory.Delete(keys, recursive: true);
            }
        }
    }

    private static async Task StartAndStopAsync(ScratchDatabase database, string keys)
    {
        await using var app = await GatewayApp.BuildAsync(
            database.ApplicationArgs($"--DataProtection:KeysDirectory={keys}"),
            builder =>
            {
                builder.Services.RemoveAll<IDeviceDriverFactory>();
                builder.Services.AddSingleton<IDeviceDriverFactory>(new FakeDriverFactory(FakeDriverFactory.Key));
            });

        await app.StartAsync();
        await app.StopAsync();
    }

    private static IReadOnlyList<string> KeyFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, "key-*.xml").Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal).ToList()
            : [];
}

using Npgsql;
using ScadaDarbox.Core.Alarms;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Historian;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Configuration;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.RealTime;
using ScadaDarbox.Gateway.Scanning;
using ScadaDarbox.Modules.Drivers.Modbus;
using ScadaDarbox.Modules.Drivers.OpcUa;
using ScadaDarbox.Persistence.TimescaleDb;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("ScadaDb")
    ?? throw new InvalidOperationException("Connection string 'ScadaDb' is not configured.");

// Migrations run before anything else touches the database, and before the host starts
// serving (ADR-0007). A failure throws, so the process never serves requests against a
// schema in an unknown state.
DatabaseMigrator.Migrate(connectionString);

// The database is prepared before the host is built so that the tag catalogue is a
// fully-formed value by the time anything can resolve it, rather than a half-populated
// singleton that fills in later.
var dataSource = NpgsqlDataSource.Create(connectionString);
await DemoConfigurationSeeder.SeedIfEmptyAsync(
    dataSource,
    builder.Configuration.GetValue("Modbus:Host", "127.0.0.1")!,
    builder.Configuration.GetValue("Modbus:Port", 5502),
    CancellationToken.None);

var configurationStore = new PostgresConfigurationStore(dataSource);
var catalogSource = new TagCatalogSource(
    await ConfigurationReloader.BuildAsync(configurationStore, CancellationToken.None));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton(catalogSource);
builder.Services.AddSingleton<IConfigurationStore>(configurationStore);
builder.Services.AddSingleton<ConfigurationReloader>();
builder.Services.AddSingleton<IFolderRepository, FolderRepository>();
builder.Services.AddSingleton<IAlarmDefinitionRepository, AlarmDefinitionRepository>();
builder.Services.AddSingleton<IDeviceTemplateRepository, DeviceTemplateRepository>();
builder.Services.AddSingleton<IDeviceRepository, DeviceRepository>();
builder.Services.AddSingleton<ITagRepository, TagRepository>();
builder.Services.AddSingleton<IHistorian, TimescaleHistorian>();
builder.Services.AddSingleton<ITagValueSubscriber, SignalRTagBroadcaster>();
builder.Services.AddSingleton<ITagEngine, TagEngine>();

// One AlarmEngine wearing two hats: it is fed by the tag engine's fan-out and read by
// the API, so it must be the same instance in both roles rather than two that disagree
// about which alarms are standing.
builder.Services.AddSingleton<IAlarmSubscriber, SignalRAlarmBroadcaster>();
builder.Services.AddSingleton<AlarmEngine>();
builder.Services.AddSingleton<IAlarmEngine>(services => services.GetRequiredService<AlarmEngine>());
builder.Services.AddSingleton<ITagValueSubscriber>(services => services.GetRequiredService<AlarmEngine>());

// Compile-time composition of driver modules (ADR-0002): each is referenced as a project
// and registered here by hand. Nothing is scanned for or loaded dynamically.
builder.Services.AddSingleton<IDeviceDriverFactory, ModbusTcpDriverFactory>();
builder.Services.AddSingleton<IDeviceDriverFactory, OpcUaDriverFactory>();

builder.Services.AddHostedService<DeviceScannerService>();
builder.Services.AddSignalR();

const string WebClientCors = "web-client";
builder.Services.AddCors(options => options.AddPolicy(WebClientCors, policy => policy
    .WithOrigins(builder.Configuration.GetSection("WebClientOrigins").Get<string[]>()
                 ?? ["http://localhost:4200"])
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

app.UseCors(WebClientCors);

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/tags", (ITagEngine engine, TagCatalogSource catalogSource) =>
{
    var tagCatalog = catalogSource.Current;
    // Tags with no reading yet are still listed, so the client shows the configured
    // hierarchy rather than an empty page until the first scan completes.
    var current = engine.GetAllCurrent().ToDictionary(s => s.TagId);

    var tags = tagCatalog.Tags
        .OrderBy(tag => tagCatalog.PathOf(tag.Id), StringComparer.OrdinalIgnoreCase)
        .Select(tag => current.TryGetValue(tag.Id, out var snapshot)
            ? TagSnapshotDto.From(snapshot)
            : new TagSnapshotDto(
                tag.Id,
                tagCatalog.PathOf(tag.Id),
                TagValueDto.None,
                DateTimeOffset.MinValue,
                "Bad",
                tag.Unit?.Symbol));

    return Results.Ok(tags);
});

app.MapGet("/api/tags/{tagId:guid}", (Guid tagId, ITagEngine engine) =>
{
    var snapshot = engine.GetCurrent(tagId);
    return snapshot is null ? Results.NotFound() : Results.Ok(TagSnapshotDto.From(snapshot));
});

app.MapGet("/api/tags/{tagId:guid}/history", async (
    Guid tagId,
    DateTimeOffset? from,
    DateTimeOffset? to,
    IHistorian historian,
    ITagRepository tags,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    var now = timeProvider.GetUtcNow();
    var samples = await historian.ReadAsync(
        tagId,
        from ?? now.AddMinutes(-15),
        to ?? now,
        cancellationToken);

    // Resolved past any deletion, so history for a device retired last year still reads
    // as its name rather than a bare identifier (ADR-0001, ADR-0009).
    var identity = await tags.FindIdentityIncludingDeletedAsync(tagId, cancellationToken);

    return Results.Ok(new TagHistoryDto(
        tagId,
        identity?.TagName,
        identity?.DeviceName,
        identity?.IsDeleted ?? false,
        samples.Select(sample => new HistorySampleDto(
            TagValueDto.From(sample.Value),
            sample.SourceTimestampUtc,
            sample.IngestedAtUtc,
            sample.Quality.ToString())).ToList()));
});

app.MapConfigurationApi();
app.MapAlarmApi();
app.MapTemplateApi();

app.MapHub<TagHub>("/hubs/tags");

await app.RunAsync();

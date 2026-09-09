using Npgsql;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>
/// Creates the schema if it is not already present. Configuration and time-series data
/// live in one engine (ADR-0006), so both are created here.
/// </summary>
public static class DatabaseSchema
{
    /// <remarks>
    /// The sample table is a plain TimescaleDB hypertable. Continuous aggregates and
    /// native compression are deliberately not used: ADR-0006 flags them as TSL-licensed
    /// pending legal review, and the Phase 1 test gate does not need them.
    /// </remarks>
    private const string Sql = """
        CREATE EXTENSION IF NOT EXISTS timescaledb;

        CREATE TABLE IF NOT EXISTS tenant (
            id   uuid PRIMARY KEY,
            name text NOT NULL
        );

        CREATE TABLE IF NOT EXISTS site (
            id           uuid PRIMARY KEY,
            tenant_id    uuid NOT NULL REFERENCES tenant (id),
            name         text NOT NULL,
            time_zone_id text NOT NULL DEFAULT 'UTC'
        );

        CREATE TABLE IF NOT EXISTS device (
            id                  uuid PRIMARY KEY,
            site_id             uuid NOT NULL REFERENCES site (id),
            name                text NOT NULL,
            driver_key          text NOT NULL,
            connection_settings jsonb NOT NULL DEFAULT '{}'::jsonb,
            scan_interval_ms    integer NOT NULL DEFAULT 1000
        );

        -- A tag's unit is stored as a dimension plus its affine conversion to SI
        -- (ADR-0005); unit_symbol is display only and is never the identity of the unit.
        CREATE TABLE IF NOT EXISTS tag (
            id                uuid PRIMARY KEY,
            device_id         uuid NOT NULL REFERENCES device (id),
            name              text NOT NULL,
            value_kind        smallint NOT NULL,
            unit_symbol       text,
            unit_dimension    smallint,
            unit_factor_to_si double precision,
            unit_offset_to_si double precision,
            source_address    text NOT NULL,
            is_writable       boolean NOT NULL DEFAULT false
        );

        -- One typed column per TagValue kind (ADR-0003), rather than a generic blob.
        -- Deliberately no foreign key to tag: history outlives configuration edits and
        -- is referenced only by the tag's stable ID (ADR-0001).
        CREATE TABLE IF NOT EXISTS tag_sample (
            tag_id         uuid NOT NULL,
            source_time    timestamptz NOT NULL,
            ingested_at    timestamptz NOT NULL,
            quality        smallint NOT NULL,
            value_kind     smallint NOT NULL,
            numeric_value  double precision,
            boolean_value  boolean,
            text_value     text,
            discrete_code  integer,
            discrete_label text
        );

        SELECT create_hypertable('tag_sample', 'source_time', if_not_exists => TRUE);

        CREATE INDEX IF NOT EXISTS ix_tag_sample_tag_time
            ON tag_sample (tag_id, source_time DESC);
        """;

    public static async Task ApplyAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(Sql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

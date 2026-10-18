using Microsoft.Data.Sqlite;

namespace Part2.OmopWriter;

/// <summary>
/// The SQLite file: three OMOP CDM 5.4 tables (with all their columns, the unused ones left NULL) plus
/// two etl_* tables the loader needs for idempotency and quarantine. The etl_* tables are not OMOP.
/// </summary>
internal static class OmopDatabase
{
    public static readonly string[] Tables =
    {
        "person", "visit_occurrence", "condition_occurrence", "etl_event", "etl_quarantine",
    };

    private const string Schema = @"
        CREATE TABLE IF NOT EXISTS person (
            person_id                   INTEGER PRIMARY KEY,
            gender_concept_id           INTEGER NOT NULL,
            year_of_birth               INTEGER NOT NULL,
            month_of_birth              INTEGER NULL,
            day_of_birth                INTEGER NULL,
            birth_datetime              TEXT    NULL,
            race_concept_id             INTEGER NOT NULL,
            ethnicity_concept_id        INTEGER NOT NULL,
            location_id                 INTEGER NULL,
            provider_id                 INTEGER NULL,
            care_site_id                INTEGER NULL,
            person_source_value         TEXT    NULL,
            gender_source_value         TEXT    NULL,
            gender_source_concept_id    INTEGER NULL,
            race_source_value           TEXT    NULL,
            race_source_concept_id      INTEGER NULL,
            ethnicity_source_value      TEXT    NULL,
            ethnicity_source_concept_id INTEGER NULL
        );
        -- One row per real patient: the natural key is '{canonical issuer}:{patient id}'.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_person_source ON person (person_source_value);

        CREATE TABLE IF NOT EXISTS visit_occurrence (
            visit_occurrence_id           INTEGER PRIMARY KEY,
            person_id                     INTEGER NOT NULL REFERENCES person (person_id),
            visit_concept_id              INTEGER NOT NULL,
            visit_start_date              TEXT    NOT NULL,
            visit_start_datetime          TEXT    NULL,
            visit_end_date                TEXT    NOT NULL,
            visit_end_datetime            TEXT    NULL,
            visit_type_concept_id         INTEGER NOT NULL,
            provider_id                   INTEGER NULL,
            care_site_id                  INTEGER NULL,
            visit_source_value            TEXT    NULL,
            visit_source_concept_id       INTEGER NULL,
            admitted_from_concept_id      INTEGER NULL,
            admitted_from_source_value    TEXT    NULL,
            discharged_to_concept_id      INTEGER NULL,
            discharged_to_source_value    TEXT    NULL,
            preceding_visit_occurrence_id INTEGER NULL
        );
        -- A person has many visits; one row per source visit ('hl7:{patient}:{admit}' or 'fhir:{encounter id}').
        CREATE UNIQUE INDEX IF NOT EXISTS ux_visit_source ON visit_occurrence (person_id, visit_source_value);

        CREATE TABLE IF NOT EXISTS condition_occurrence (
            condition_occurrence_id       INTEGER PRIMARY KEY,
            person_id                     INTEGER NOT NULL REFERENCES person (person_id),
            condition_concept_id          INTEGER NOT NULL,
            condition_start_date          TEXT    NOT NULL,
            condition_start_datetime      TEXT    NULL,
            condition_end_date            TEXT    NULL,
            condition_end_datetime        TEXT    NULL,
            condition_type_concept_id     INTEGER NOT NULL,
            condition_status_concept_id   INTEGER NULL,
            stop_reason                   TEXT    NULL,
            provider_id                   INTEGER NULL,
            visit_occurrence_id           INTEGER NULL REFERENCES visit_occurrence (visit_occurrence_id),
            visit_detail_id               INTEGER NULL,
            condition_source_value        TEXT    NULL,
            condition_source_concept_id   INTEGER NULL,
            condition_status_source_value TEXT    NULL
        );
        -- Diagnoses accumulate per visit (D23): one row per source code on a visit.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_condition_visit_source ON condition_occurrence (visit_occurrence_id, condition_source_value);

        -- Every event loaded, with the hash that was loaded: this tells a retransmission from a conflict.
        CREATE TABLE IF NOT EXISTS etl_event (
            event_id        TEXT    PRIMARY KEY,
            content_hash    TEXT    NOT NULL,
            sent_at         TEXT    NOT NULL,
            source_file     TEXT    NOT NULL,
            source_position INTEGER NOT NULL
        );

        -- Readable events that cannot go into OMOP, with the reason and the canonical line to replay.
        CREATE TABLE IF NOT EXISTS etl_quarantine (
            event_id     TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            reason       TEXT NOT NULL,
            line         TEXT NOT NULL,
            UNIQUE (event_id, content_hash)
        );";

    /// <summary>Opens (or creates) the database file, with foreign keys on and the schema in place.</summary>
    public static SqliteConnection Open(string path)
    {
        // No pooling: a batch run holds one connection, and the file must be free when the run ends.
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = ON;" + Schema;
            command.ExecuteNonQuery();
        }

        return connection;
    }

    public static long CountRows(SqliteConnection connection, string table)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            // The table name comes from the Tables constant only, never from input.
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            return (long)command.ExecuteScalar()!;
        }
    }
}

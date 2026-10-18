using Microsoft.Data.Sqlite;
using Part2.Canonical;
using Part2.OmopWriter;

namespace Part2.Tests;

/// <summary>Writer tests over the real sample data: both inputs ingested, then loaded into a temporary SQLite file.</summary>
public sealed class OmopLoaderTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"part2-{Guid.NewGuid()}.db");

    private static readonly IReadOnlyList<CanonicalEvent> AllEvents =
        [.. Fixtures.IngestHl7().Events, .. Fixtures.IngestFhir().Events];

    public void Dispose() => File.Delete(_databasePath);

    private LoadReport Load(IEnumerable<CanonicalEvent> events)
    {
        using var connection = OmopDatabase.Open(_databasePath);
        var loader = new OmopLoader(
            connection,
            CsvConceptMapper.Load(Fixtures.PathOf("vocab_icd10.csv")),
            IssuerAliases.Load(Fixtures.PathOf("issuers.json")));
        return loader.Load(events.Select((e, i) => new SourceLine("test.jsonl", i + 1, CanonicalJson.Serialize(e))));
    }

    private List<string> Query(string sql)
    {
        using var connection = OmopDatabase.Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i).ToString())));
        }

        return rows;
    }

    private Dictionary<string, long> RowCounts()
    {
        using var connection = OmopDatabase.Open(_databasePath);
        return OmopDatabase.Tables.ToDictionary(t => t, t => OmopDatabase.CountRows(connection, t));
    }

    [Fact]
    public void Running_the_writer_twice_leaves_the_same_rows()
    {
        var first = Load(AllEvents);
        var countsAfterFirstRun = RowCounts();
        var second = Load(AllEvents);

        Assert.Equal(countsAfterFirstRun, RowCounts());
        Assert.Equal(0, second.Loaded);
        Assert.Equal(first.Loaded + first.AlreadyLoaded, second.AlreadyLoaded);
        // Expected for the sample data; see the README for the reasoning behind each number.
        Assert.Equal(12, countsAfterFirstRun["person"]);
        Assert.Equal(16, countsAfterFirstRun["visit_occurrence"]);
        Assert.Equal(15, countsAfterFirstRun["condition_occurrence"]);
    }

    [Fact]
    public void Retransmitted_Adt00013_is_loaded_once()
    {
        var report = Load(AllEvents);

        Assert.Equal(1, report.AlreadyLoaded); // the second copy, in the very first run
        Assert.Equal(["1"], Query("""
            SELECT COUNT(*) FROM visit_occurrence v JOIN person p ON p.person_id = v.person_id
            WHERE p.person_source_value = 'SAN_ANDRES:100234567'
            """));
    }

    [Fact]
    public void Code_without_mapping_loads_with_concept_0_and_keeps_the_source_code()
    {
        Load(AllEvents);

        var rows = Query("""
            SELECT condition_source_value, condition_concept_id FROM condition_occurrence
            WHERE condition_source_value IN ('C18.2', 'C18.99', 'C25.9') ORDER BY condition_source_value
            """);
        // C18.2 twice: TORRES has it on her HL7 visit and on her FHIR encounter, which are not merged (D22).
        Assert.Equal(["C18.2|4112752", "C18.2|4112752", "C18.99|0", "C25.9|0"], rows);
    }

    [Fact]
    public void Patients_shared_by_hl7_and_fhir_are_one_person_with_visits_from_both()
    {
        Load(AllEvents);

        var rows = Query("""
            SELECT p.person_source_value, COUNT(*), GROUP_CONCAT(v.visit_source_value, ' ')
            FROM person p JOIN visit_occurrence v ON v.person_id = p.person_id
            WHERE p.person_source_value IN ('SAN_ANDRES:100034567', 'SAN_ANDRES:100123456')
            GROUP BY p.person_id ORDER BY p.person_source_value
            """);
        Assert.Equal(2, rows.Count);
        Assert.StartsWith("SAN_ANDRES:100034567|3|", rows[0]); // 1 HL7 visit + 2 FHIR encounters, not merged (D22)
        Assert.StartsWith("SAN_ANDRES:100123456|2|", rows[1]);
        Assert.Empty(Query("SELECT 1 FROM person WHERE person_source_value LIKE 'urn:oid:%'"));
    }

    [Fact]
    public void A08_replaces_the_diagnoses_of_the_visit()
    {
        Load(AllEvents);

        // ADT00002 sent R07.9 for this ER visit; the later ADT00015 (A08) sent I21.4.
        Assert.Equal(["I21.4"], Query("""
            SELECT c.condition_source_value FROM condition_occurrence c
            JOIN visit_occurrence v ON v.visit_occurrence_id = c.visit_occurrence_id
            WHERE v.visit_source_value = 'hl7:100023456:20260315083200'
            """));
    }

    [Fact]
    public void An_older_message_arriving_late_does_not_undo_a_newer_diagnosis_set()
    {
        Load(AllEvents);
        var original = AllEvents.Single(e => e.Source.RecordId == "ADT00002");
        var lateReplay = original with
        {
            EventId = original.EventId + "-replayed",
            ContentHash = "late",
            Diagnoses = [original.Diagnoses[0] with { Code = "R07.89" }],
        };

        var report = Load([lateReplay]);

        Assert.Equal(1, report.SupersededDiagnosisSets);
        Assert.Equal(["I21.4"], Query("""
            SELECT c.condition_source_value FROM condition_occurrence c
            JOIN visit_occurrence v ON v.visit_occurrence_id = c.visit_occurrence_id
            WHERE v.visit_source_value = 'hl7:100023456:20260315083200'
            """));
    }

    [Fact]
    public void Same_event_id_with_another_hash_is_a_conflict_and_the_loaded_data_is_kept()
    {
        Load(AllEvents);
        var original = AllEvents.Single(e => e.Source.RecordId == "ADT00014");
        var tampered = original with
        {
            ContentHash = "different",
            Diagnoses = [original.Diagnoses[0] with { Code = "G30.9" }],
        };

        var report = Load([tampered]);

        Assert.Equal(1, report.Conflicts);
        Assert.Empty(Query("SELECT 1 FROM condition_occurrence WHERE condition_source_value = 'G30.9'"));
        Assert.Single(Query("SELECT reason FROM etl_quarantine WHERE event_id = 'hl7:HIS_SANANDRES:ADT00014' AND content_hash = 'different'"));
    }

    [Fact]
    public void Missing_birth_date_quarantines_the_event_instead_of_inventing_a_year()
    {
        Load(AllEvents);

        Assert.Empty(Query("SELECT 1 FROM person WHERE person_source_value = 'SAN_ANDRES:100089012'"));
        var reason = Assert.Single(Query("SELECT reason FROM etl_quarantine WHERE event_id = 'hl7:HIS_SANANDRES:ADT00009'"));
        Assert.Contains("year_of_birth", reason);
    }

    [Fact]
    public void Diagnosis_whose_concept_is_not_in_the_condition_domain_is_quarantined()
    {
        Load(AllEvents);

        Assert.Empty(Query("SELECT 1 FROM condition_occurrence WHERE condition_source_value = 'O80'"));
        Assert.Contains("domain 'Observation'", Assert.Single(Query("SELECT reason FROM etl_quarantine WHERE item = 'diagnosis:O80'")));
    }

    [Fact]
    public void Discharge_closes_the_visit_and_open_visits_end_on_their_start_date()
    {
        Load(AllEvents);

        var rows = Query("""
            SELECT visit_source_value, visit_end_date, IFNULL(visit_end_datetime, 'NULL') FROM visit_occurrence
            WHERE visit_source_value IN ('hl7:100012345:20260315080500', 'fhir:enc-013-1') ORDER BY visit_source_value
            """);
        Assert.Equal(
            ["fhir:enc-013-1|2026-03-14|NULL", "hl7:100012345:20260315080500|2026-03-15|2026-03-15 13:30:00"],
            rows);
    }

    [Fact]
    public void A_line_that_breaks_the_contract_is_quarantined_not_fatal()
    {
        using (var connection = OmopDatabase.Open(_databasePath))
        {
            var loader = new OmopLoader(
                connection,
                CsvConceptMapper.Load(Fixtures.PathOf("vocab_icd10.csv")),
                IssuerAliases.Load(Fixtures.PathOf("issuers.json")));
            var report = loader.Load([new SourceLine("bad.jsonl", 1, "{\"schema_version\":2}")]);
            Assert.Equal(1, report.InvalidLines);
        }

        Assert.Equal(["line:bad.jsonl:1"], Query("SELECT event_id FROM etl_quarantine"));
    }

    [Fact]
    public void Csv_line_parser_handles_quoted_commas()
    {
        Assert.Equal(
            ["I50.9", "ICD10CM", "Heart failure, unspecified", "316139"],
            CsvConceptMapper.ParseLine("I50.9,ICD10CM,\"Heart failure, unspecified\",316139"));
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Part2.Canonical;

namespace Part2.OmopWriter;

/// <summary>One line of a canonical JSONL file, with where it came from.</summary>
internal sealed record SourceLine(string File, int Number, string Text);

/// <summary>What one run did. A rerun on the same input reports everything as AlreadyLoaded.</summary>
internal sealed class LoadReport
{
    public int Loaded { get; set; }
    public int AlreadyLoaded { get; set; }
    public int Conflicts { get; set; }
    public int QuarantinedEvents { get; set; }
    public int QuarantinedDiagnoses { get; set; }
    public int SupersededDiagnosisSets { get; set; }
    public int InvalidLines { get; set; }
}

/// <summary>
/// Loads canonical events into person, visit_occurrence and condition_occurrence. Idempotent:
/// an event already loaded with the same content_hash is skipped, so a rerun or a replay changes nothing.
/// </summary>
internal sealed class OmopLoader(SqliteConnection connection, IConceptMapper conceptMapper, IssuerAliases issuers)
{
    // OMOP standard concepts. Gender and visit concepts are the OHDSI standard ones; 32817 = "EHR" type concept.
    private const int MaleConceptId = 8507;
    private const int FemaleConceptId = 8532;
    private const int InpatientVisitConceptId = 9201;
    private const int OutpatientVisitConceptId = 9202;
    private const int EmergencyRoomVisitConceptId = 9203;
    private const int EhrTypeConceptId = 32817;
    private const int NoMatchingConceptId = 0;
    private const string ConditionDomain = "Condition";

    private SqliteTransaction? _transaction;

    public LoadReport Load(IEnumerable<SourceLine> lines)
    {
        var report = new LoadReport();
        using var transaction = connection.BeginTransaction();
        _transaction = transaction;

        var events = new List<CanonicalEvent>();
        foreach (var line in lines)
        {
            try
            {
                events.Add(CanonicalJson.Deserialize(line.Text));
            }
            catch (JsonException ex)
            {
                Quarantine($"line:{line.File}:{line.Number}", Sha256Hex(line.Text), "event",
                    $"breaks the canonical contract: {ex.Message}", line.Text);
                report.InvalidLines++;
            }
        }

        // D23: by sent_at, so within a run the most recent message is applied last.
        var ordered = events
            .OrderBy(e => e.Source.SentAt)
            .ThenBy(e => e.Source.File, StringComparer.Ordinal)
            .ThenBy(e => e.Source.Position);
        foreach (var canonicalEvent in ordered)
        {
            LoadEvent(canonicalEvent, report);
        }

        transaction.Commit();
        _transaction = null;
        return report;
    }

    private void LoadEvent(CanonicalEvent e, LoadReport report)
    {
        // D17: same event_id + same hash = retransmission; same event_id + other hash = conflict.
        var loadedHash = (string?)Scalar("SELECT content_hash FROM etl_event WHERE event_id = $id", ("$id", e.EventId));
        if (loadedHash == e.ContentHash)
        {
            report.AlreadyLoaded++;
            return;
        }

        if (loadedHash is not null)
        {
            Quarantine(e, "event", $"conflict: event_id already loaded with content_hash {loadedHash}; loaded data kept");
            report.Conflicts++;
            return;
        }

        // D13: OMOP requires year_of_birth, and a birth year is never invented.
        if (e.Patient.BirthDate is null)
        {
            Quarantine(e, "event", "no birth date: OMOP person.year_of_birth is required");
            report.QuarantinedEvents++;
            return;
        }

        var personId = UpsertPerson(e.Patient);
        if (e.Visit is not null)
        {
            var visitId = UpsertVisit(personId, e.Source.Format, e.Visit);
            ReplaceDiagnoses(personId, visitId, e, report);
        }

        Execute("""
            INSERT INTO etl_event (event_id, content_hash, sent_at, source_file, source_position)
            VALUES ($id, $hash, $sentAt, $file, $position)
            """,
            ("$id", e.EventId), ("$hash", e.ContentHash), ("$sentAt", DateTimeText(e.Source.SentAt)),
            ("$file", e.Source.File), ("$position", e.Source.Position));
        report.Loaded++;
    }

    /// <summary>One row per real patient (D24): the issuer is canonicalized before building the key. First event wins.</summary>
    private long UpsertPerson(PatientInfo patient)
    {
        var sourceValue = $"{issuers.Canonicalize(patient.Issuer)}:{patient.SourceId}";
        var birthDate = patient.BirthDate!.Value;
        Execute("""
            INSERT INTO person (gender_concept_id, year_of_birth, month_of_birth, day_of_birth,
                                race_concept_id, ethnicity_concept_id, person_source_value,
                                gender_source_value, gender_source_concept_id)
            VALUES ($gender, $year, $month, $day, 0, 0, $source, $genderSource, 0)
            ON CONFLICT (person_source_value) DO NOTHING
            """,
            ("$gender", patient.Sex switch
            {
                Sex.Male => MaleConceptId,
                Sex.Female => FemaleConceptId,
                _ => NoMatchingConceptId,
            }),
            ("$year", birthDate.Year), ("$month", birthDate.Month), ("$day", birthDate.Day),
            ("$source", sourceValue), ("$genderSource", patient.SexSource));
        return (long)Scalar("SELECT person_id FROM person WHERE person_source_value = $source", ("$source", sourceValue))!;
    }

    /// <summary>
    /// One row per source visit. Without an end, visit_end_date = start date and visit_end_datetime stays
    /// NULL (D14). A later event that brings a real end (an A03) updates it. HL7 and FHIR visits are never merged (D22).
    /// </summary>
    private long UpsertVisit(long personId, SourceFormat format, VisitInfo visit)
    {
        var sourceValue = $"{(format == SourceFormat.Hl7v2 ? "hl7" : "fhir")}:{visit.SourceId}";
        Execute("""
            INSERT INTO visit_occurrence (person_id, visit_concept_id, visit_start_date, visit_start_datetime,
                                          visit_end_date, visit_end_datetime, visit_type_concept_id, visit_source_value)
            VALUES ($person, $concept, $startDate, $start, $endDate, $end, $type, $source)
            ON CONFLICT (person_id, visit_source_value) DO UPDATE
                SET visit_end_date = excluded.visit_end_date, visit_end_datetime = excluded.visit_end_datetime
                WHERE excluded.visit_end_datetime IS NOT NULL
            """,
            ("$person", personId),
            ("$concept", visit.Class switch
            {
                VisitClass.Inpatient => InpatientVisitConceptId,
                VisitClass.Outpatient => OutpatientVisitConceptId,
                VisitClass.Emergency => EmergencyRoomVisitConceptId,
                _ => NoMatchingConceptId,
            }),
            ("$startDate", DateText(visit.Start)), ("$start", DateTimeText(visit.Start)),
            ("$endDate", DateText(visit.End ?? visit.Start)), ("$end", visit.End is { } end ? DateTimeText(end) : null),
            ("$type", EhrTypeConceptId), ("$source", sourceValue));
        return (long)Scalar(
            "SELECT visit_occurrence_id FROM visit_occurrence WHERE person_id = $person AND visit_source_value = $source",
            ("$person", personId), ("$source", sourceValue))!;
    }

    /// <summary>
    /// D23: the diagnosis set of the most recent message replaces the previous one for the visit.
    /// D27: an event without diagnoses leaves the visit's set as it is.
    /// </summary>
    private void ReplaceDiagnoses(long personId, long visitId, CanonicalEvent e, LoadReport report)
    {
        if (e.Diagnoses.Count == 0)
        {
            return;
        }

        var sentAt = DateTimeText(e.Source.SentAt);
        var appliedSentAt = (string?)Scalar(
            "SELECT sent_at FROM etl_visit_diagnoses WHERE visit_occurrence_id = $visit", ("$visit", visitId));
        if (appliedSentAt is not null && string.CompareOrdinal(appliedSentAt, sentAt) > 0)
        {
            // An older message arriving late (for example a DLQ replay) must not undo a newer one.
            report.SupersededDiagnosisSets++;
            return;
        }

        Execute("DELETE FROM condition_occurrence WHERE visit_occurrence_id = $visit", ("$visit", visitId));
        foreach (var diagnosis in e.Diagnoses)
        {
            var mapping = conceptMapper.Map(diagnosis.System, diagnosis.Code);

            // D26: OMOP puts a record in the table of its concept's domain; only Condition is in scope.
            if (mapping is not null && mapping.DomainId != ConditionDomain)
            {
                Quarantine(e, $"diagnosis:{diagnosis.Code}",
                    $"maps to concept {mapping.ConceptId} in domain '{mapping.DomainId}'; only domain '{ConditionDomain}' is loaded");
                report.QuarantinedDiagnoses++;
                continue;
            }

            // D11: no mapping → concept 0, and the source code is kept in condition_source_value.
            Execute("""
                INSERT INTO condition_occurrence (person_id, condition_concept_id, condition_start_date,
                                                  condition_type_concept_id, visit_occurrence_id,
                                                  condition_source_value, condition_source_concept_id)
                VALUES ($person, $concept, $startDate, $type, $visit, $source, 0)
                """,
                ("$person", personId), ("$concept", mapping?.ConceptId ?? NoMatchingConceptId),
                ("$startDate", diagnosis.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DateText(e.Visit!.Start)),
                ("$type", EhrTypeConceptId), ("$visit", visitId), ("$source", diagnosis.Code));
        }

        Execute("""
            INSERT INTO etl_visit_diagnoses (visit_occurrence_id, event_id, sent_at) VALUES ($visit, $id, $sentAt)
            ON CONFLICT (visit_occurrence_id) DO UPDATE SET event_id = excluded.event_id, sent_at = excluded.sent_at
            """,
            ("$visit", visitId), ("$id", e.EventId), ("$sentAt", sentAt));
    }

    private void Quarantine(CanonicalEvent e, string item, string reason) =>
        Quarantine(e.EventId, e.ContentHash, item, reason, CanonicalJson.Serialize(e));

    // INSERT OR IGNORE: quarantining the same thing twice (a rerun) leaves one row.
    private void Quarantine(string eventId, string contentHash, string item, string reason, string line) =>
        Execute("""
            INSERT OR IGNORE INTO etl_quarantine (event_id, content_hash, item, reason, line)
            VALUES ($id, $hash, $item, $reason, $line)
            """,
            ("$id", eventId), ("$hash", contentHash), ("$item", item), ("$reason", reason), ("$line", line));

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteScalar() is var value and not DBNull ? value : null;
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static string DateText(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string DateTimeText(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

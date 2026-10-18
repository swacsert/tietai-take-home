using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Part2.Canonical;

namespace Part2.OmopWriter;

/// <summary>One line of a canonical JSONL file, with the file it came from and its line number.</summary>
internal sealed record SourceLine(string File, int Number, string Text);

/// <summary>What one run did. A rerun on the same input reports every event as AlreadyLoaded.</summary>
internal sealed class LoadReport
{
    public int Loaded { get; set; }
    public int AlreadyLoaded { get; set; }
    public int Conflicts { get; set; }
    public int QuarantinedEvents { get; set; }
    public int InvalidLines { get; set; }

    /// <summary>Diagnoses not loaded because their concept's domain table is out of scope, by domain.</summary>
    public Dictionary<string, int> OutOfScopeByDomain { get; } = new Dictionary<string, int>();
}

/// <summary>
/// Loads canonical events into person, visit_occurrence and condition_occurrence. It is idempotent:
/// an event already loaded with the same content_hash is skipped, so a rerun or a replay changes nothing.
/// </summary>
internal sealed class OmopLoader
{
    // OMOP standard concepts. Gender and visit concepts are the OHDSI standard ones; 32817 is the "EHR" type concept.
    private const int MaleConceptId = 8507;
    private const int FemaleConceptId = 8532;
    private const int InpatientVisitConceptId = 9201;
    private const int OutpatientVisitConceptId = 9202;
    private const int EmergencyRoomVisitConceptId = 9203;
    private const int EhrTypeConceptId = 32817;
    private const int NoMatchingConceptId = 0;

    // Domains whose records belong in condition_occurrence. "Symptom" is not an OMOP domain: in OMOP,
    // symptoms are Condition-domain concepts, so the stub's "Symptom" rows load as conditions. (D26)
    private static readonly HashSet<string> ConditionTableDomains = new HashSet<string> { "Condition", "Symptom" };

    private readonly SqliteConnection _connection;
    private readonly IConceptMapper _conceptMapper;
    private readonly IssuerAliases _issuers;
    private SqliteTransaction? _transaction;

    public OmopLoader(SqliteConnection connection, IConceptMapper conceptMapper, IssuerAliases issuers)
    {
        _connection = connection;
        _conceptMapper = conceptMapper;
        _issuers = issuers;
    }

    /// <summary>Loads all the lines in one transaction: a run is applied completely or not at all.</summary>
    public LoadReport Load(IEnumerable<SourceLine> lines)
    {
        var report = new LoadReport();
        using (SqliteTransaction transaction = _connection.BeginTransaction())
        {
            _transaction = transaction;

            List<CanonicalEvent> events = ParseLines(lines, report);
            foreach (CanonicalEvent canonicalEvent in OrderBySentAt(events))
            {
                LoadEvent(canonicalEvent, report);
            }

            transaction.Commit();
            _transaction = null;
        }

        return report;
    }

    // A line that breaks the contract is quarantined; the rest of the run goes on.
    private List<CanonicalEvent> ParseLines(IEnumerable<SourceLine> lines, LoadReport report)
    {
        var events = new List<CanonicalEvent>();
        foreach (SourceLine line in lines)
        {
            try
            {
                events.Add(CanonicalJson.Deserialize(line.Text));
            }
            catch (JsonException ex)
            {
                string eventId = $"line:{line.File}:{line.Number}";
                Quarantine(eventId, Sha256Hex(line.Text), $"breaks the canonical contract: {ex.Message}", line.Text);
                report.InvalidLines++;
            }
        }

        return events;
    }

    // Oldest message first, so the result does not depend on file order: the first event fixes a
    // person's demographics and a condition's start date. Ties are broken by file and position. (D23)
    private static IEnumerable<CanonicalEvent> OrderBySentAt(List<CanonicalEvent> events)
    {
        return events
            .OrderBy(e => e.Source.SentAt)
            .ThenBy(e => e.Source.File, StringComparer.Ordinal)
            .ThenBy(e => e.Source.Position);
    }

    // Each event ends in exactly one of four outcomes: already loaded, conflict, quarantine or insert.
    private void LoadEvent(CanonicalEvent e, LoadReport report)
    {
        string? loadedHash = FindLoadedContentHash(e.EventId);

        if (loadedHash == e.ContentHash)
        {
            // Same event_id and same content: a retransmission or a rerun. Nothing to do. (D17)
            report.AlreadyLoaded++;
        }
        else if (loadedHash != null)
        {
            QuarantineConflict(e, loadedHash);
            report.Conflicts++;
        }
        else if (e.Patient.BirthDate == null)
        {
            QuarantineMissingBirthDate(e);
            report.QuarantinedEvents++;
        }
        else
        {
            InsertEvent(e, report);
            report.Loaded++;
        }
    }

    private string? FindLoadedContentHash(string eventId)
    {
        return (string?)Scalar("SELECT content_hash FROM etl_event WHERE event_id = $id", ("$id", eventId));
    }

    // Same event_id but different content: the sender changed a message it had already sent. The data
    // already loaded is kept, and the new version waits in quarantine for someone to decide. (D17)
    private void QuarantineConflict(CanonicalEvent e, string loadedHash)
    {
        Quarantine(e, $"conflict: event_id already loaded with content_hash {loadedHash}; loaded data kept");
    }

    // OMOP requires year_of_birth, and a birth year is never invented. (D13)
    private void QuarantineMissingBirthDate(CanonicalEvent e)
    {
        Quarantine(e, "no birth date: OMOP person.year_of_birth is required");
    }

    private void InsertEvent(CanonicalEvent e, LoadReport report)
    {
        long personId = UpsertPerson(e.Patient);
        if (e.Visit != null)
        {
            long visitId = UpsertVisit(personId, e.Source.Format, e.Visit);
            AddDiagnoses(personId, visitId, e.Visit, e.Diagnoses, report);
        }

        RecordLoadedEvent(e);
    }

    private void RecordLoadedEvent(CanonicalEvent e)
    {
        Execute(@"
            INSERT INTO etl_event (event_id, content_hash, sent_at, source_file, source_position)
            VALUES ($id, $hash, $sentAt, $file, $position)",
            ("$id", e.EventId), ("$hash", e.ContentHash), ("$sentAt", DateTimeText(e.Source.SentAt)),
            ("$file", e.Source.File), ("$position", e.Source.Position));
    }

    // One row per real patient: the issuer is canonicalized before the key is built, so HL7 and FHIR
    // land on the same row. The first event wins; later events do not change the row. (D24)
    private long UpsertPerson(PatientInfo patient)
    {
        string sourceValue = _issuers.Canonicalize(patient.Issuer) + ":" + patient.SourceId;
        DateOnly birthDate = patient.BirthDate!.Value;

        Execute(@"
            INSERT INTO person (gender_concept_id, year_of_birth, month_of_birth, day_of_birth,
                                race_concept_id, ethnicity_concept_id, person_source_value,
                                gender_source_value, gender_source_concept_id)
            VALUES ($gender, $year, $month, $day, 0, 0, $source, $genderSource, 0)
            ON CONFLICT (person_source_value) DO NOTHING",
            ("$gender", GenderConceptId(patient.Sex)),
            ("$year", birthDate.Year), ("$month", birthDate.Month), ("$day", birthDate.Day),
            ("$source", sourceValue), ("$genderSource", patient.SexSource));

        return (long)Scalar("SELECT person_id FROM person WHERE person_source_value = $source", ("$source", sourceValue))!;
    }

    private static int GenderConceptId(Sex? sex)
    {
        if (sex == Sex.Male)
        {
            return MaleConceptId;
        }
        else if (sex == Sex.Female)
        {
            return FemaleConceptId;
        }

        return NoMatchingConceptId;
    }

    // One row per source visit. HL7 and FHIR visits are never merged (D22). Without an end, the end date
    // is the start date and visit_end_datetime stays NULL (D14); a later event that brings a real end
    // (an A03) updates both.
    private long UpsertVisit(long personId, SourceFormat format, VisitInfo visit)
    {
        string sourcePrefix = format == SourceFormat.Hl7v2 ? "hl7" : "fhir";
        string sourceValue = sourcePrefix + ":" + visit.SourceId;
        DateTime endDate = visit.End ?? visit.Start;
        string? endDateTime = null;
        if (visit.End != null)
        {
            endDateTime = DateTimeText(visit.End.Value);
        }

        Execute(@"
            INSERT INTO visit_occurrence (person_id, visit_concept_id, visit_start_date, visit_start_datetime,
                                          visit_end_date, visit_end_datetime, visit_type_concept_id, visit_source_value)
            VALUES ($person, $concept, $startDate, $start, $endDate, $end, $type, $source)
            ON CONFLICT (person_id, visit_source_value) DO UPDATE
                SET visit_end_date = excluded.visit_end_date, visit_end_datetime = excluded.visit_end_datetime
                WHERE excluded.visit_end_datetime IS NOT NULL",
            ("$person", personId), ("$concept", VisitConceptId(visit.Class)),
            ("$startDate", DateText(visit.Start)), ("$start", DateTimeText(visit.Start)),
            ("$endDate", DateText(endDate)), ("$end", endDateTime),
            ("$type", EhrTypeConceptId), ("$source", sourceValue));

        return (long)Scalar(
            "SELECT visit_occurrence_id FROM visit_occurrence WHERE person_id = $person AND visit_source_value = $source",
            ("$person", personId), ("$source", sourceValue))!;
    }

    private static int VisitConceptId(VisitClass visitClass)
    {
        if (visitClass == VisitClass.Inpatient)
        {
            return InpatientVisitConceptId;
        }
        else if (visitClass == VisitClass.Outpatient)
        {
            return OutpatientVisitConceptId;
        }
        else if (visitClass == VisitClass.Emergency)
        {
            return EmergencyRoomVisitConceptId;
        }

        return NoMatchingConceptId;
    }

    // Diagnoses accumulate on the visit and are never deleted: a code already on the visit stays as it
    // is, and a new code is added. Withdrawing a diagnosis is an open question for the customer. (D23)
    private void AddDiagnoses(long personId, long visitId, VisitInfo visit, IReadOnlyList<Diagnosis> diagnoses, LoadReport report)
    {
        foreach (Diagnosis diagnosis in diagnoses)
        {
            ConceptMapping? mapping = _conceptMapper.Map(diagnosis.System, diagnosis.Code);
            if (IsOutOfScopeDomain(mapping))
            {
                CountOutOfScope(report, mapping!.DomainId);
            }
            else
            {
                InsertCondition(personId, visitId, visit, diagnosis, mapping);
            }
        }
    }

    // OMOP puts a record in the table of its concept's domain. A code without mapping has no domain and
    // is loaded as a condition with concept 0 (D11); a domain other than Condition is out of scope (D26).
    private static bool IsOutOfScopeDomain(ConceptMapping? mapping)
    {
        return mapping != null && !ConditionTableDomains.Contains(mapping.DomainId);
    }

    private static void CountOutOfScope(LoadReport report, string domain)
    {
        report.OutOfScopeByDomain.TryGetValue(domain, out int count);
        report.OutOfScopeByDomain[domain] = count + 1;
    }

    // No mapping means concept 0, with the source code kept in condition_source_value. (D11)
    // The same code on the same visit is one row, however many messages send it. (D23)
    private void InsertCondition(long personId, long visitId, VisitInfo visit, Diagnosis diagnosis, ConceptMapping? mapping)
    {
        int conceptId = mapping != null ? mapping.ConceptId : NoMatchingConceptId;

        Execute(@"
            INSERT INTO condition_occurrence (person_id, condition_concept_id, condition_start_date,
                                              condition_type_concept_id, visit_occurrence_id,
                                              condition_source_value, condition_source_concept_id)
            VALUES ($person, $concept, $startDate, $type, $visit, $source, 0)
            ON CONFLICT (visit_occurrence_id, condition_source_value) DO NOTHING",
            ("$person", personId), ("$concept", conceptId), ("$startDate", ConditionStartDate(diagnosis, visit)),
            ("$type", EhrTypeConceptId), ("$visit", visitId), ("$source", diagnosis.Code));
    }

    // OMOP requires condition_start_date. When the source has no date, the visit start date is used.
    private static string ConditionStartDate(Diagnosis diagnosis, VisitInfo visit)
    {
        if (diagnosis.StartDate != null)
        {
            return diagnosis.StartDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return DateText(visit.Start);
    }

    private void Quarantine(CanonicalEvent e, string reason)
    {
        Quarantine(e.EventId, e.ContentHash, reason, CanonicalJson.Serialize(e));
    }

    // INSERT OR IGNORE: quarantining the same event twice (a rerun) leaves one row.
    private void Quarantine(string eventId, string contentHash, string reason, string line)
    {
        Execute(@"
            INSERT OR IGNORE INTO etl_quarantine (event_id, content_hash, reason, line)
            VALUES ($id, $hash, $reason, $line)",
            ("$id", eventId), ("$hash", contentHash), ("$reason", reason), ("$line", line));
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using (SqliteCommand command = CreateCommand(sql, parameters))
        {
            command.ExecuteNonQuery();
        }
    }

    // Returns the first column of the first row, or null when there is no row or the value is NULL.
    private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using (SqliteCommand command = CreateCommand(sql, parameters))
        {
            object? value = command.ExecuteScalar();
            if (value == null || value is DBNull)
            {
                return null;
            }

            return value;
        }
    }

    private SqliteCommand CreateCommand(string sql, (string Name, object? Value)[] parameters)
    {
        SqliteCommand command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static string DateText(DateTime value)
    {
        return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string DateTimeText(DateTime value)
    {
        return value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string Sha256Hex(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash);
    }
}

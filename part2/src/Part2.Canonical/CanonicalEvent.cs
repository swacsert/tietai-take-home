namespace Part2.Canonical;

/// <summary>
/// One line of the canonical JSONL: one HL7 message, or one FHIR Encounter with the
/// resources linked to it. The contract is documented in docs/canonical-event.md.
/// </summary>
public sealed record CanonicalEvent(
    int SchemaVersion,
    string EventId,
    string ContentHash,
    SourceInfo Source,
    PatientInfo Patient,
    VisitInfo? Visit,
    IReadOnlyList<Diagnosis> Diagnoses,
    IReadOnlyList<UnmappedItem> Unmapped,
    IReadOnlyList<QualityWarning> QualityWarnings)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record SourceInfo(
    SourceFormat Format,
    string File,
    int Position,
    string RecordId,
    string RecordType,
    DateTime SentAt,
    string? Sender,
    string? EncodingCharacters);

public sealed record PatientInfo(
    string SourceId,
    string Issuer,
    DateOnly? BirthDate,
    Sex? Sex,
    string? SexSource);

public sealed record VisitInfo(
    string SourceId,
    VisitClass Class,
    string ClassSource,
    DateTime Start,
    DateTime? End);

public sealed record Diagnosis(
    string Code,
    string System,
    string SystemSource,
    string? Display,
    DateOnly? StartDate);

/// <summary>A source segment or resource the ingestor does not map, kept whole and raw.</summary>
public sealed record UnmappedItem(UnmappedKind Kind, string Name, string Raw);

/// <summary>Something odd the ingestor accepted; not visible from the typed fields alone.</summary>
public sealed record QualityWarning(string Code, string Location, string Detail);

public enum SourceFormat { Hl7v2, FhirR4 }

public enum Sex { Female, Male, Other, Unknown }

public enum VisitClass { Inpatient, Outpatient, Emergency }

public enum UnmappedKind { Hl7Segment, FhirResource }

using System.Globalization;
using Part2.Canonical;

namespace Part2.Ingest.Hl7;

/// <summary>
/// Maps one parsed HL7 message to a canonical event (contract: docs/canonical-event.md).
/// DLQ reasons name the field, never its value: the reason ends up in logs, the value may be patient data.
/// </summary>
internal static class Hl7EventMapper
{
    // Segments read into typed fields. Every other segment (Z-segments included) goes whole to unmapped.
    private static readonly HashSet<string> MappedSegments = ["MSH", "EVN", "PID", "PV1", "DG1"];

    private static readonly string[] TimestampFormats = ["yyyyMMddHHmmss", "yyyyMMddHHmm", "yyyyMMdd"];

    public static CanonicalEvent Map(Hl7Message message, string fileName, int position)
    {
        var warnings = new List<QualityWarning>();
        var msh = message.Segments[0];
        var evn = AtMostOne(message, "EVN");
        var pid = AtMostOne(message, "PID")
            ?? throw new InvalidMessageException("no PID segment: the message cannot be attributed to a patient");
        var pv1 = AtMostOne(message, "PV1");
        var dg1s = message.Segments.Where(s => s.Name == "DG1").ToList();
        if (pv1 is null && dg1s.Count > 0)
        {
            throw new InvalidMessageException("DG1 without PV1: the diagnoses cannot be attached to a visit");
        }

        var sender = Required(msh.Component(3, 1), "MSH-3");
        var controlId = Required(msh.Field(10), "MSH-10");
        var sentAt = ParseTimestamp(Required(msh.Component(7, 1), "MSH-7"), "MSH-7");

        // MSH-9 is split with the declared component separator, never guessed. ADT00010 declares '@'
        // but writes MSH-9 with '^', so there it stays one component and we say so.
        var messageType = msh.Field(9);
        if (message.ComponentSeparator != '^' && messageType.Contains('^'))
        {
            warnings.Add(new QualityWarning("component_separator_mismatch", "MSH-9",
                $"contains '^' but MSH-2 declares '{message.ComponentSeparator}'; parsed with the declared separator"));
        }

        var typeParts = messageType.Split(message.ComponentSeparator);
        var triggerEvent = typeParts.Length > 1 ? typeParts[1] : "";

        var source = new SourceInfo(
            SourceFormat.Hl7v2,
            fileName,
            position,
            controlId,
            string.Join('^', typeParts),
            sentAt,
            sender,
            message.FieldSeparator + message.EncodingCharacters);

        var patient = MapPatient(pid);
        var visit = pv1 is null ? null : MapVisit(pv1, evn, triggerEvent, patient.SourceId, warnings);
        var diagnoses = dg1s.Select(MapDiagnosis).ToList();
        var unmapped = message.Segments
            .Where(s => !MappedSegments.Contains(s.Name))
            .Select(s => new UnmappedItem(UnmappedKind.Hl7Segment, s.Name, s.Raw))
            .ToList();
        warnings.AddRange(ControlCharacterWarnings(message));

        return new CanonicalEvent(
            CanonicalEvent.CurrentSchemaVersion,
            $"hl7:{sender}:{controlId}",
            ContentHash(message),
            source,
            patient,
            visit,
            diagnoses,
            unmapped,
            warnings);
    }

    private static PatientInfo MapPatient(Hl7Segment pid)
    {
        var birthDate = pid.Component(7, 1);
        var sex = pid.Field(8);
        return new PatientInfo(
            Required(pid.Component(3, 1), "PID-3.1"),
            Required(pid.Component(3, 4), "PID-3.4"),
            birthDate.Length == 0 ? null : ParseDate(birthDate, "PID-7"),
            sex switch
            {
                "" => null,
                "F" => Sex.Female,
                "M" => Sex.Male,
                "O" => Sex.Other,
                _ => Sex.Unknown,
            },
            sex.Length == 0 ? null : sex);
    }

    private static VisitInfo MapVisit(
        Hl7Segment pv1, Hl7Segment? evn, string triggerEvent, string patientId, List<QualityWarning> warnings)
    {
        // D29: this HIS sends emergency visits as PV1-2 = O with hospital service PV1-10 = ER.
        var classSource = pv1.Field(2);
        var visitClass = (classSource, HospitalService: pv1.Component(10, 1)) switch
        {
            ("E", _) or (_, "ER") => VisitClass.Emergency,
            ("I", _) => VisitClass.Inpatient,
            ("O", _) => VisitClass.Outpatient,
            _ => throw new InvalidMessageException("PV1-2 patient class is not supported"),
        };

        // PV1-44 is the standard place. This sender uses PV1-48 instead (decision D21).
        var admit = pv1.Component(44, 1);
        var admitLocation = "PV1-44";
        if (admit.Length == 0 && pv1.Component(48, 1).Length > 0)
        {
            admit = pv1.Component(48, 1);
            admitLocation = "PV1-48";
            warnings.Add(new QualityWarning("admit_datetime_nonstandard_field", "PV1-48",
                "admit datetime in non-standard field (PV1-44 is empty)"));
        }

        var start = ParseTimestamp(Required(admit, "PV1-44 and PV1-48"), admitLocation);

        DateTime? end = null;
        var discharge = pv1.Component(45, 1);
        if (discharge.Length > 0)
        {
            end = ParseTimestamp(discharge, "PV1-45");
        }
        else if (triggerEvent == "A03" && evn?.Component(2, 1) is { Length: > 0 } recorded)
        {
            end = ParseTimestamp(recorded, "EVN-2");
            warnings.Add(new QualityWarning("discharge_datetime_from_evn", "EVN-2",
                "A03 without PV1-45; discharge datetime taken from EVN-2"));
        }

        // PV1-19 (visit number) is unusable in this feed, so the visit key is derived from the
        // patient and the full admit datetime as received (decision D18).
        return new VisitInfo($"{patientId}:{admit}", visitClass, classSource, start, end);
    }

    private static Diagnosis MapDiagnosis(Hl7Segment dg1)
    {
        var systemSource = Required(dg1.Component(3, 3), "DG1-3.3");
        var display = dg1.Component(3, 2);
        var date = dg1.Component(5, 1);
        return new Diagnosis(
            Required(dg1.Component(3, 1), "DG1-3.1"),
            Normalization.CodeSystem(systemSource),
            systemSource,
            display.Length == 0 ? null : display,
            date.Length == 0 ? null : ParseDate(date, "DG1-5"));
    }

    private static IEnumerable<QualityWarning> ControlCharacterWarnings(Hl7Message message)
    {
        foreach (var segment in message.Segments)
        {
            for (var number = 1; number <= segment.FieldCount; number++)
            {
                foreach (var c in segment.Field(number).Where(char.IsControl))
                {
                    yield return new QualityWarning("control_character", $"{segment.Name}-{number}",
                        $"byte 0x{(int)c:X2} kept as received");
                }
            }
        }
    }

    /// <summary>
    /// SHA-256 of the message without its transmission timestamps (MSH-7, EVN-2), so a resend of the
    /// same content has the same hash. Segments are joined with \r whatever line endings arrived.
    /// </summary>
    private static string ContentHash(Hl7Message message) =>
        Normalization.Sha256Hex(string.Join('\r', message.Segments.Select(s => s.Name switch
        {
            "MSH" => s.RawWithFieldEmptied(7),
            "EVN" => s.RawWithFieldEmptied(2),
            _ => s.Raw,
        })));

    private static Hl7Segment? AtMostOne(Hl7Message message, string name)
    {
        var found = message.Segments.Where(s => s.Name == name).ToList();
        return found.Count <= 1
            ? found.SingleOrDefault()
            : throw new InvalidMessageException($"{name} appears {found.Count} times; expected at most one");
    }

    private static string Required(string value, string location) =>
        value.Length > 0 ? value : throw new InvalidMessageException($"{location} is empty");

    private static DateTime ParseTimestamp(string value, string location) =>
        DateTime.TryParseExact(value, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : throw new InvalidMessageException($"{location} is not a supported HL7 timestamp");

    private static DateOnly ParseDate(string value, string location) =>
        value.Length >= 8 && DateOnly.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : throw new InvalidMessageException($"{location} is not a supported HL7 date");
}

using System.Globalization;
using Part2.Canonical;

namespace Part2.Ingest.Hl7;

/// <summary>
/// Maps one parsed HL7 message to a canonical event (contract: docs/canonical-event.md).
/// DLQ reasons name the field, never its value: the reason ends up in logs, and the value may be patient data.
/// </summary>
internal static class Hl7EventMapper
{
    // Segments read into typed fields. Every other segment, Z-segments included, is kept whole in "unmapped".
    private static readonly HashSet<string> MappedSegments = new HashSet<string> { "MSH", "EVN", "PID", "PV1", "DG1" };

    private static readonly string[] TimestampFormats = { "yyyyMMddHHmmss", "yyyyMMddHHmm", "yyyyMMdd" };

    public static CanonicalEvent Map(Hl7Message message, string fileName, int position)
    {
        var warnings = new List<QualityWarning>();

        Hl7Segment msh = message.Segments[0];
        Hl7Segment? evn = FindAtMostOne(message, "EVN");
        Hl7Segment pid = FindPid(message);
        Hl7Segment? pv1 = FindAtMostOne(message, "PV1");
        List<Hl7Segment> dg1s = message.Segments.Where(s => s.Name == "DG1").ToList();
        if (pv1 == null && dg1s.Count > 0)
        {
            throw new InvalidMessageException("DG1 without PV1: the diagnoses cannot be attached to a visit");
        }

        string[] messageType = SplitMessageType(message, msh, warnings);
        string triggerEvent = messageType.Length > 1 ? messageType[1] : "";
        SourceInfo source = MapSource(message, msh, messageType, fileName, position);

        PatientInfo patient = MapPatient(pid);
        VisitInfo? visit = null;
        if (pv1 != null)
        {
            visit = MapVisit(pv1, evn, triggerEvent, patient.SourceId, warnings);
        }

        List<Diagnosis> diagnoses = dg1s.Select(MapDiagnosis).ToList();
        List<UnmappedItem> unmapped = UnmappedSegments(message);
        warnings.AddRange(FindControlCharacters(message));

        return new CanonicalEvent(
            CanonicalEvent.CurrentSchemaVersion,
            $"hl7:{source.Sender}:{source.RecordId}",
            ContentHash(message),
            source,
            patient,
            visit,
            diagnoses,
            unmapped,
            warnings);
    }

    private static SourceInfo MapSource(Hl7Message message, Hl7Segment msh, string[] messageType, string fileName, int position)
    {
        string sender = Required(msh.Component(3, 1), "MSH-3");
        string controlId = Required(msh.Field(10), "MSH-10");
        DateTime sentAt = ParseTimestamp(Required(msh.Component(7, 1), "MSH-7"), "MSH-7");
        string recordType = string.Join('^', messageType);
        string encodingCharacters = message.FieldSeparator + message.EncodingCharacters;

        return new SourceInfo(SourceFormat.Hl7v2, fileName, position, controlId, recordType, sentAt, sender, encodingCharacters);
    }

    // Splits MSH-9 with the component separator the message declares, never a guessed one. ADT00010
    // declares '@' but writes MSH-9 with '^', so there MSH-9 stays one component, with a warning. (D25)
    private static string[] SplitMessageType(Hl7Message message, Hl7Segment msh, List<QualityWarning> warnings)
    {
        string messageType = msh.Field(9);
        if (message.ComponentSeparator != '^' && messageType.Contains('^'))
        {
            warnings.Add(new QualityWarning("component_separator_mismatch", "MSH-9",
                $"contains '^' but MSH-2 declares '{message.ComponentSeparator}'; parsed with the declared separator"));
        }

        return messageType.Split(message.ComponentSeparator);
    }

    private static PatientInfo MapPatient(Hl7Segment pid)
    {
        string patientId = Required(pid.Component(3, 1), "PID-3.1");
        string issuer = Required(pid.Component(3, 4), "PID-3.4");

        // A missing birth date stays null: it is never invented. (D13)
        DateOnly? birthDate = null;
        string birthDateText = pid.Component(7, 1);
        if (birthDateText.Length > 0)
        {
            birthDate = ParseDate(birthDateText, "PID-7");
        }

        string sexCode = pid.Field(8);
        string? sexSource = sexCode.Length > 0 ? sexCode : null;
        return new PatientInfo(patientId, issuer, birthDate, MapSex(sexCode), sexSource);
    }

    // HL7 table 0001. An empty PID-8 means "not sent" (null); a code we do not know means "unknown".
    private static Sex? MapSex(string code)
    {
        switch (code)
        {
            case "":
                return null;
            case "F":
                return Sex.Female;
            case "M":
                return Sex.Male;
            case "O":
                return Sex.Other;
            default:
                return Sex.Unknown;
        }
    }

    private static VisitInfo MapVisit(
        Hl7Segment pv1, Hl7Segment? evn, string triggerEvent, string patientId, List<QualityWarning> warnings)
    {
        VisitClass visitClass = MapVisitClass(pv1);
        string admit = ReadAdmitDateTime(pv1, warnings, out string admitLocation);
        DateTime start = ParseTimestamp(admit, admitLocation);
        DateTime? end = ReadDischargeDateTime(pv1, evn, triggerEvent, warnings);

        // PV1-19 (visit number) is unusable in this feed, so the visit key is the patient plus the full
        // admit datetime as received. (D18)
        string visitKey = patientId + ":" + admit;
        return new VisitInfo(visitKey, visitClass, pv1.Field(2), start, end);
    }

    // This HIS sends emergency visits as PV1-2 = O with hospital service PV1-10 = ER. (D29)
    private static VisitClass MapVisitClass(Hl7Segment pv1)
    {
        string patientClass = pv1.Field(2);
        string hospitalService = pv1.Component(10, 1);

        if (patientClass == "E" || hospitalService == "ER")
        {
            return VisitClass.Emergency;
        }
        else if (patientClass == "I")
        {
            return VisitClass.Inpatient;
        }
        else if (patientClass == "O")
        {
            return VisitClass.Outpatient;
        }

        throw new InvalidMessageException("PV1-2 patient class is not supported");
    }

    // PV1-44 is the standard field for the admit datetime. This sender uses PV1-48 instead, so PV1-48 is
    // read when PV1-44 is empty, with a warning. (D21)
    private static string ReadAdmitDateTime(Hl7Segment pv1, List<QualityWarning> warnings, out string location)
    {
        string standardField = pv1.Component(44, 1);
        string nonStandardField = pv1.Component(48, 1);

        if (standardField.Length == 0 && nonStandardField.Length > 0)
        {
            warnings.Add(new QualityWarning("admit_datetime_nonstandard_field", "PV1-48",
                "admit datetime in non-standard field (PV1-44 is empty)"));
            location = "PV1-48";
            return nonStandardField;
        }

        location = "PV1-44";
        return Required(standardField, "PV1-44 and PV1-48");
    }

    // PV1-45 when the sender filled it. A discharge (A03) without it takes the time from EVN-2, with a
    // warning. Only for A03: in an A01, A04 or A08, EVN-2 is the time of the admission or of the update. (D28)
    private static DateTime? ReadDischargeDateTime(
        Hl7Segment pv1, Hl7Segment? evn, string triggerEvent, List<QualityWarning> warnings)
    {
        string discharge = pv1.Component(45, 1);
        if (discharge.Length > 0)
        {
            return ParseTimestamp(discharge, "PV1-45");
        }

        if (triggerEvent != "A03" || evn == null)
        {
            return null;
        }

        string recorded = evn.Component(2, 1);
        if (recorded.Length == 0)
        {
            return null;
        }

        DateTime end = ParseTimestamp(recorded, "EVN-2");
        warnings.Add(new QualityWarning("discharge_datetime_from_evn", "EVN-2",
            "A03 without PV1-45; discharge datetime taken from EVN-2"));
        return end;
    }

    private static Diagnosis MapDiagnosis(Hl7Segment dg1)
    {
        string systemSource = Required(dg1.Component(3, 3), "DG1-3.3");
        string code = Required(dg1.Component(3, 1), "DG1-3.1");
        string display = dg1.Component(3, 2);

        DateOnly? date = null;
        string dateText = dg1.Component(5, 1);
        if (dateText.Length > 0)
        {
            date = ParseDate(dateText, "DG1-5");
        }

        return new Diagnosis(code, Normalization.CodeSystem(systemSource), systemSource, display.Length > 0 ? display : null, date);
    }

    // Every segment this mapper does not read, kept whole and raw, so nothing is dropped silently.
    private static List<UnmappedItem> UnmappedSegments(Hl7Message message)
    {
        var unmapped = new List<UnmappedItem>();
        foreach (Hl7Segment segment in message.Segments)
        {
            if (!MappedSegments.Contains(segment.Name))
            {
                unmapped.Add(new UnmappedItem(UnmappedKind.Hl7Segment, segment.Name, segment.Raw));
            }
        }

        return unmapped;
    }

    // A control byte (such as 0x07 in ADT00014's ZPV-3) is kept as received and reported. (D16)
    private static List<QualityWarning> FindControlCharacters(Hl7Message message)
    {
        var warnings = new List<QualityWarning>();
        foreach (Hl7Segment segment in message.Segments)
        {
            for (int number = 1; number <= segment.FieldCount; number++)
            {
                foreach (char c in segment.Field(number))
                {
                    if (char.IsControl(c))
                    {
                        warnings.Add(new QualityWarning("control_character", $"{segment.Name}-{number}",
                            $"byte 0x{(int)c:X2} kept as received"));
                    }
                }
            }
        }

        return warnings;
    }

    // SHA-256 of the message without its transmission timestamps (MSH-7 and EVN-2), so a resend of the
    // same content gets the same hash. Segments are joined with \r whatever line endings arrived. (D17)
    private static string ContentHash(Hl7Message message)
    {
        var segments = new List<string>();
        foreach (Hl7Segment segment in message.Segments)
        {
            if (segment.Name == "MSH")
            {
                segments.Add(segment.RawWithFieldEmptied(7));
            }
            else if (segment.Name == "EVN")
            {
                segments.Add(segment.RawWithFieldEmptied(2));
            }
            else
            {
                segments.Add(segment.Raw);
            }
        }

        return Normalization.Sha256Hex(string.Join('\r', segments));
    }

    private static Hl7Segment FindPid(Hl7Message message)
    {
        Hl7Segment? pid = FindAtMostOne(message, "PID");
        if (pid == null)
        {
            throw new InvalidMessageException("no PID segment: the message cannot be attributed to a patient");
        }

        return pid;
    }

    private static Hl7Segment? FindAtMostOne(Hl7Message message, string name)
    {
        List<Hl7Segment> found = message.Segments.Where(s => s.Name == name).ToList();
        if (found.Count > 1)
        {
            throw new InvalidMessageException($"{name} appears {found.Count} times; expected at most one");
        }

        return found.Count == 1 ? found[0] : null;
    }

    private static string Required(string value, string location)
    {
        if (value.Length == 0)
        {
            throw new InvalidMessageException($"{location} is empty");
        }

        return value;
    }

    private static DateTime ParseTimestamp(string value, string location)
    {
        if (DateTime.TryParseExact(value, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
        {
            return parsed;
        }

        throw new InvalidMessageException($"{location} is not a supported HL7 timestamp");
    }

    // HL7 dates may carry a time after the first eight digits; only the date part is used.
    private static DateOnly ParseDate(string value, string location)
    {
        if (value.Length >= 8
            && DateOnly.TryParseExact(value.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
        {
            return parsed;
        }

        throw new InvalidMessageException($"{location} is not a supported HL7 date");
    }
}

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Part2.Canonical;

namespace Part2.Ingest.Fhir;

/// <summary>
/// Turns a FHIR R4 Bundle into one canonical event per Encounter, with its Patient and the resources
/// that reference it. Resources that hang from no Encounter are counted, never dropped silently. (D19)
/// DLQ reasons name the element, never its value (see Hl7EventMapper).
/// </summary>
internal static class FhirBundleMapper
{
    private static readonly JsonWriterOptions MinifiedJson =
        new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>One bundle entry: its position in the bundle (from 1), the resource, its type and its id.</summary>
    private sealed record Entry(int Position, JsonElement Resource, string Type, string? Id);

    public static IngestResult Map(string json, string fileName)
    {
        try
        {
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                return MapBundle(document.RootElement, fileName);
            }
        }
        catch (JsonException ex)
        {
            // The file is not JSON. JsonException's own message can quote the input, so only the position is kept.
            return WholeFileDeadLetter(json, $"bundle rejected: not valid JSON (line {ex.LineNumber + 1}, byte {ex.BytePositionInLine})");
        }
        catch (InvalidMessageException ex)
        {
            return WholeFileDeadLetter(json, $"bundle rejected: {ex.Message}");
        }
    }

    private static IngestResult MapBundle(JsonElement bundle, string fileName)
    {
        // Step 1: read the bundle header and index its entries.
        CheckIsBundle(bundle);
        string bundleId = ReadBundleId(bundle);
        DateTime sentAt = ReadBundleTimestamp(bundle);
        List<Entry> entries = ReadEntries(bundle);
        Dictionary<string, Entry> patients = IndexPatients(entries);
        List<Entry> encounters = entries.Where(e => e.Type == "Encounter").ToList();

        // Step 2: group the other resources by the Encounter they reference.
        Dictionary<string, List<Entry>> linkedByEncounter = GroupByEncounter(entries, encounters);

        // Step 3: map each Encounter to one event. A bad Encounter goes to the DLQ and the rest go on.
        var events = new List<CanonicalEvent>();
        var deadLetters = new List<DeadLetter>();
        foreach (Entry encounter in encounters)
        {
            List<Entry> linked = LinkedTo(encounter, linkedByEncounter);
            try
            {
                events.Add(MapEncounter(bundleId, sentAt, fileName, encounter, patients, linked));
            }
            catch (InvalidMessageException ex)
            {
                // The Encounter's JSON as it is in the bundle. Its Patient and linked resources stay in the input file.
                deadLetters.Add(new DeadLetter(encounter.Position, encounter.Resource.GetRawText(), ex.Message));
            }
        }

        // Step 4: count what hangs from no Encounter.
        Dictionary<string, int> unlinked = CountUnlinked(entries, encounters, patients, linkedByEncounter);
        return new IngestResult(events, deadLetters, unlinked);
    }

    private static void CheckIsBundle(JsonElement bundle)
    {
        if (Text(bundle, "resourceType") != "Bundle")
        {
            throw new InvalidMessageException("root resourceType is not Bundle");
        }
    }

    private static string ReadBundleId(JsonElement bundle)
    {
        string? bundleId = Text(bundle, "id");
        if (bundleId == null)
        {
            throw new InvalidMessageException("Bundle.id is missing");
        }

        return bundleId;
    }

    private static DateTime ReadBundleTimestamp(JsonElement bundle)
    {
        DateTime? timestamp = ParseDateTime(Text(bundle, "timestamp"), "Bundle.timestamp");
        if (timestamp == null)
        {
            throw new InvalidMessageException("Bundle.timestamp is missing");
        }

        return timestamp.Value;
    }

    private static List<Entry> ReadEntries(JsonElement bundle)
    {
        if (!bundle.TryGetProperty("entry", out JsonElement entryArray) || entryArray.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidMessageException("Bundle.entry is missing");
        }

        var entries = new List<Entry>();
        int position = 0;
        foreach (JsonElement entry in entryArray.EnumerateArray())
        {
            position++;
            if (entry.TryGetProperty("resource", out JsonElement resource))
            {
                string type = Text(resource, "resourceType") ?? "(no resourceType)";
                entries.Add(new Entry(position, resource, type, Text(resource, "id")));
            }
            else
            {
                entries.Add(new Entry(position, entry, "(entry without resource)", null));
            }
        }

        return entries;
    }

    private static Dictionary<string, Entry> IndexPatients(List<Entry> entries)
    {
        var patients = new Dictionary<string, Entry>();
        foreach (Entry entry in entries)
        {
            if (entry.Type != "Patient" || entry.Id == null)
            {
                continue;
            }

            if (!patients.TryAdd(entry.Id, entry))
            {
                throw new InvalidMessageException($"a Patient id appears twice (entry {entry.Position})");
            }
        }

        return patients;
    }

    // Every resource that is not a Patient or an Encounter, filed under the Encounter it references
    // ("encounter", or "context" in MedicationStatement). References to Encounters not in the bundle are left out.
    private static Dictionary<string, List<Entry>> GroupByEncounter(List<Entry> entries, List<Entry> encounters)
    {
        var groups = new Dictionary<string, List<Entry>>();
        foreach (Entry encounter in encounters)
        {
            if (encounter.Id != null)
            {
                groups[encounter.Id] = new List<Entry>();
            }
        }

        foreach (Entry entry in entries)
        {
            if (IsPatientOrEncounter(entry))
            {
                continue;
            }

            string? encounterId = ReferencedId(entry.Resource, "Encounter", "encounter", "context");
            if (encounterId != null && groups.TryGetValue(encounterId, out List<Entry>? group))
            {
                group.Add(entry);
            }
        }

        return groups;
    }

    private static List<Entry> LinkedTo(Entry encounter, Dictionary<string, List<Entry>> linkedByEncounter)
    {
        if (encounter.Id != null && linkedByEncounter.TryGetValue(encounter.Id, out List<Entry>? linked))
        {
            return linked;
        }

        return new List<Entry>();
    }

    // Counts, by resource type, the resources linked to no Encounter in the bundle and the Patients
    // that no Encounter uses.
    private static Dictionary<string, int> CountUnlinked(
        List<Entry> entries, List<Entry> encounters, Dictionary<string, Entry> patients, Dictionary<string, List<Entry>> linkedByEncounter)
    {
        var counts = new Dictionary<string, int>();
        foreach (Entry entry in entries)
        {
            if (IsPatientOrEncounter(entry))
            {
                continue;
            }

            string? encounterId = ReferencedId(entry.Resource, "Encounter", "encounter", "context");
            if (encounterId == null || !linkedByEncounter.ContainsKey(encounterId))
            {
                Increment(counts, entry.Type);
            }
        }

        var referencedPatients = new HashSet<string>();
        foreach (Entry encounter in encounters)
        {
            string? patientId = ReferencedId(encounter.Resource, "Patient", "subject");
            if (patientId != null)
            {
                referencedPatients.Add(patientId);
            }
        }

        foreach (string patientId in patients.Keys)
        {
            if (!referencedPatients.Contains(patientId))
            {
                Increment(counts, "Patient");
            }
        }

        return counts;
    }

    private static CanonicalEvent MapEncounter(
        string bundleId, DateTime sentAt, string fileName, Entry encounter, Dictionary<string, Entry> patients, List<Entry> linked)
    {
        if (encounter.Id == null)
        {
            throw new InvalidMessageException("Encounter without id");
        }

        Entry patient = FindPatient(encounter, patients);

        var conditions = new List<Entry>();
        var unmapped = new List<UnmappedItem>();
        foreach (Entry entry in linked)
        {
            if (entry.Type == "Condition")
            {
                conditions.Add(entry);
            }
            else
            {
                unmapped.Add(new UnmappedItem(UnmappedKind.FhirResource, entry.Type, Minify(entry.Resource)));
            }
        }

        string contentHash = ContentHash(patient, encounter, linked);
        var source = new SourceInfo(SourceFormat.FhirR4, fileName, encounter.Position, encounter.Id, "Encounter", sentAt, null, null);
        PatientInfo patientInfo = MapPatient(patient.Resource);
        VisitInfo visit = MapVisit(encounter.Id, encounter.Resource);
        List<Diagnosis> diagnoses = conditions.Select(c => MapCondition(c.Resource)).ToList();

        return new CanonicalEvent(
            CanonicalEvent.CurrentSchemaVersion,
            $"fhir:{bundleId}:Encounter/{encounter.Id}",
            contentHash,
            source,
            patientInfo,
            visit,
            diagnoses,
            unmapped,
            new List<QualityWarning>());
    }

    private static Entry FindPatient(Entry encounter, Dictionary<string, Entry> patients)
    {
        string? patientId = ReferencedId(encounter.Resource, "Patient", "subject");
        if (patientId == null)
        {
            throw new InvalidMessageException("Encounter.subject does not reference a Patient");
        }

        if (!patients.TryGetValue(patientId, out Entry? patient))
        {
            throw new InvalidMessageException("the Patient in Encounter.subject is not in the bundle");
        }

        return patient;
    }

    // The Patient first, then the Encounter, then the linked resources in bundle order, each as minified
    // JSON and joined with \n. (D17)
    private static string ContentHash(Entry patient, Entry encounter, List<Entry> linked)
    {
        var parts = new List<string>();
        parts.Add(Minify(patient.Resource));
        parts.Add(Minify(encounter.Resource));
        foreach (Entry entry in linked)
        {
            parts.Add(Minify(entry.Resource));
        }

        return Normalization.Sha256Hex(string.Join('\n', parts));
    }

    private static PatientInfo MapPatient(JsonElement patient)
    {
        JsonElement identifier = First(patient, "identifier");
        string? gender = Text(patient, "gender");
        string? birthDateText = Text(patient, "birthDate");

        string? patientId = Text(identifier, "value");
        if (patientId == null)
        {
            throw new InvalidMessageException("Patient.identifier.value is missing");
        }

        string? issuer = Text(identifier, "system");
        if (issuer == null)
        {
            throw new InvalidMessageException("Patient.identifier.system is missing");
        }

        return new PatientInfo(patientId, issuer, ParseBirthDate(birthDateText), MapGender(gender), gender);
    }

    // A missing birth date stays null (D13). A partial one ("1980") cannot fill OMOP's day and month, so it is rejected.
    private static DateOnly? ParseBirthDate(string? text)
    {
        if (text == null)
        {
            return null;
        }

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
        {
            return parsed;
        }

        throw new InvalidMessageException("Patient.birthDate is not a full date");
    }

    // FHIR administrative-gender. A missing gender stays null; a value we do not know means "unknown".
    private static Sex? MapGender(string? gender)
    {
        switch (gender)
        {
            case null:
                return null;
            case "female":
                return Sex.Female;
            case "male":
                return Sex.Male;
            case "other":
                return Sex.Other;
            default:
                return Sex.Unknown;
        }
    }

    private static VisitInfo MapVisit(string encounterId, JsonElement encounter)
    {
        // In R4, Encounter.class is a single Coding (v3 ActCode).
        string? classCode = null;
        if (encounter.TryGetProperty("class", out JsonElement coding))
        {
            classCode = Text(coding, "code");
        }

        if (classCode == null)
        {
            throw new InvalidMessageException("Encounter.class.code is missing");
        }

        VisitClass visitClass = MapEncounterClass(classCode);

        JsonElement period = default;
        if (encounter.TryGetProperty("period", out JsonElement periodElement))
        {
            period = periodElement;
        }

        DateTime? start = ParseDateTime(Text(period, "start"), "Encounter.period.start");
        if (start == null)
        {
            throw new InvalidMessageException("Encounter.period.start is missing");
        }

        DateTime? end = ParseDateTime(Text(period, "end"), "Encounter.period.end");
        return new VisitInfo(encounterId, visitClass, classCode, start.Value, end);
    }

    private static VisitClass MapEncounterClass(string classCode)
    {
        switch (classCode)
        {
            case "IMP":
                return VisitClass.Inpatient;
            case "AMB":
                return VisitClass.Outpatient;
            case "EMER":
                return VisitClass.Emergency;
            default:
                throw new InvalidMessageException("Encounter.class is not supported");
        }
    }

    // Only the first coding is read.
    private static Diagnosis MapCondition(JsonElement condition)
    {
        JsonElement coding = default;
        if (condition.TryGetProperty("code", out JsonElement code))
        {
            coding = First(code, "coding");
        }

        string? system = Text(coding, "system");
        if (system == null)
        {
            throw new InvalidMessageException("Condition.code.coding.system is missing");
        }

        DateTime? onset = ParseDateTime(Text(condition, "onsetDateTime"), "Condition.onsetDateTime");

        string? codeValue = Text(coding, "code");
        if (codeValue == null)
        {
            throw new InvalidMessageException("Condition.code.coding.code is missing");
        }

        DateOnly? startDate = null;
        if (onset != null)
        {
            startDate = DateOnly.FromDateTime(onset.Value);
        }

        return new Diagnosis(codeValue, Normalization.CodeSystem(system), system, Text(coding, "display"), startDate);
    }

    // The id in a reference such as "Encounter/enc-1", from the first of the given properties that has one.
    private static string? ReferencedId(JsonElement resource, string type, params string[] properties)
    {
        string prefix = type + "/";
        foreach (string property in properties)
        {
            if (!resource.TryGetProperty(property, out JsonElement reference))
            {
                continue;
            }

            string? value = Text(reference, "reference");
            if (value != null && value.StartsWith(prefix, StringComparison.Ordinal))
            {
                return value.Substring(prefix.Length);
            }
        }

        return null;
    }

    // FHIR datetimes carry an offset. The canonical event keeps the local wall time that the offset describes.
    private static DateTime? ParseDateTime(string? value, string location)
    {
        if (value == null)
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed))
        {
            return parsed.DateTime;
        }

        throw new InvalidMessageException($"{location} is not a valid datetime");
    }

    // The string value of a property, or null if the element is not an object or the property is not a string.
    private static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    // The first item of an array property, or an empty element (ValueKind Undefined) if there is none.
    private static JsonElement First(JsonElement element, string arrayProperty)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        if (!element.TryGetProperty(arrayProperty, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return default;
        }

        if (array.GetArrayLength() == 0)
        {
            return default;
        }

        return array[0];
    }

    private static bool IsPatientOrEncounter(Entry entry)
    {
        return entry.Type == "Patient" || entry.Type == "Encounter";
    }

    private static void Increment(Dictionary<string, int> counts, string key)
    {
        counts.TryGetValue(key, out int current);
        counts[key] = current + 1;
    }

    private static string Minify(JsonElement element)
    {
        using (var buffer = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(buffer, MinifiedJson))
            {
                element.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }

    private static IngestResult WholeFileDeadLetter(string json, string reason)
    {
        var deadLetters = new List<DeadLetter> { new DeadLetter(0, json, reason) };
        return new IngestResult(new List<CanonicalEvent>(), deadLetters, new Dictionary<string, int>());
    }
}

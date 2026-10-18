using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Part2.Canonical;

namespace Part2.Ingest.Fhir;

/// <summary>
/// Turns a FHIR R4 Bundle into one canonical event per Encounter, with its Patient and the resources
/// that reference it. Resources that hang from no Encounter are counted, never dropped silently.
/// DLQ reasons name the element, never its value (see Hl7EventMapper).
/// </summary>
internal static class FhirBundleMapper
{
    private static readonly JsonWriterOptions MinifiedJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed record Entry(int Position, JsonElement Resource, string Type, string? Id);

    public static IngestResult Map(string json, string fileName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return MapBundle(document.RootElement, fileName);
        }
        // The bundle as a whole is unusable: the full file goes to the DLQ as position 0.
        catch (JsonException ex)
        {
            // JsonException's own message can quote the input, so only its position is kept.
            return WholeFileDeadLetter(json, $"bundle rejected: not valid JSON (line {ex.LineNumber + 1}, byte {ex.BytePositionInLine})");
        }
        catch (InvalidMessageException ex)
        {
            return WholeFileDeadLetter(json, $"bundle rejected: {ex.Message}");
        }
    }

    private static IngestResult WholeFileDeadLetter(string json, string reason) =>
        new([], [new DeadLetter(0, json, reason)], new Dictionary<string, int>());

    private static IngestResult MapBundle(JsonElement bundle, string fileName)
    {
        if (Text(bundle, "resourceType") != "Bundle")
        {
            throw new InvalidMessageException("root resourceType is not Bundle");
        }

        var bundleId = Text(bundle, "id") ?? throw new InvalidMessageException("Bundle.id is missing");
        var sentAt = ParseDateTime(Text(bundle, "timestamp"), "Bundle.timestamp")
            ?? throw new InvalidMessageException("Bundle.timestamp is missing");

        var entries = bundle.TryGetProperty("entry", out var entryArray) && entryArray.ValueKind == JsonValueKind.Array
            ? entryArray.EnumerateArray()
                .Select((entry, index) => entry.TryGetProperty("resource", out var resource)
                    ? new Entry(index + 1, resource, Text(resource, "resourceType") ?? "(no resourceType)", Text(resource, "id"))
                    : new Entry(index + 1, entry, "(entry without resource)", null))
                .ToList()
            : throw new InvalidMessageException("Bundle.entry is missing");

        var patients = new Dictionary<string, Entry>();
        foreach (var patient in entries.Where(e => e.Type == "Patient" && e.Id is not null))
        {
            if (!patients.TryAdd(patient.Id!, patient))
            {
                throw new InvalidMessageException($"a Patient id appears twice (entry {patient.Position})");
            }
        }

        var encounters = entries.Where(e => e.Type == "Encounter").ToList();
        var encounterIds = encounters.Where(e => e.Id is not null).Select(e => e.Id!).ToHashSet();
        var linked = entries
            .Where(e => e.Type is not ("Patient" or "Encounter"))
            .Select(e => (Entry: e, EncounterId: ReferencedId(e.Resource, "Encounter", "encounter", "context")))
            .ToList();
        var linkedByEncounter = linked
            .Where(l => l.EncounterId is not null && encounterIds.Contains(l.EncounterId))
            .ToLookup(l => l.EncounterId!, l => l.Entry);

        var events = new List<CanonicalEvent>();
        var deadLetters = new List<DeadLetter>();
        foreach (var encounter in encounters)
        {
            var group = linkedByEncounter[encounter.Id ?? ""].ToList();
            try
            {
                events.Add(MapEncounter(bundleId, sentAt, fileName, encounter, patients, group));
            }
            catch (InvalidMessageException ex)
            {
                deadLetters.Add(new DeadLetter(encounter.Position, GroupAsBundle(bundle, encounter, patients, group), ex.Message));
            }
        }

        // Counted, not dropped: resources linked to no known Encounter, and Patients no Encounter uses.
        var referencedPatients = encounters
            .Select(e => ReferencedId(e.Resource, "Patient", "subject"))
            .ToHashSet();
        var unlinked = linked
            .Where(l => l.EncounterId is null || !encounterIds.Contains(l.EncounterId))
            .Select(l => l.Entry.Type)
            .Concat(patients.Keys.Where(id => !referencedPatients.Contains(id)).Select(_ => "Patient"))
            .CountBy(type => type)
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        return new IngestResult(events, deadLetters, unlinked);
    }

    private static CanonicalEvent MapEncounter(
        string bundleId, DateTime sentAt, string fileName, Entry encounter, Dictionary<string, Entry> patients, List<Entry> linked)
    {
        var encounterId = encounter.Id ?? throw new InvalidMessageException("Encounter without id");
        var patientId = ReferencedId(encounter.Resource, "Patient", "subject")
            ?? throw new InvalidMessageException("Encounter.subject does not reference a Patient");
        var patient = patients.GetValueOrDefault(patientId)
            ?? throw new InvalidMessageException("the Patient in Encounter.subject is not in the bundle");

        var conditions = linked.Where(e => e.Type == "Condition").ToList();
        var unmapped = linked
            .Where(e => e.Type != "Condition")
            .Select(e => new UnmappedItem(UnmappedKind.FhirResource, e.Type, Minify(e.Resource)))
            .ToList();
        IEnumerable<Entry> hashed = [patient, encounter, .. linked];

        return new CanonicalEvent(
            CanonicalEvent.CurrentSchemaVersion,
            $"fhir:{bundleId}:Encounter/{encounterId}",
            Normalization.Sha256Hex(string.Join('\n', hashed.Select(e => Minify(e.Resource)))),
            new SourceInfo(SourceFormat.FhirR4, fileName, encounter.Position, encounterId, "Encounter", sentAt, null, null),
            MapPatient(patient.Resource),
            MapVisit(encounterId, encounter.Resource),
            conditions.Select(c => MapCondition(c.Resource)).ToList(),
            unmapped,
            []);
    }

    private static PatientInfo MapPatient(JsonElement patient)
    {
        var identifier = First(patient, "identifier");
        var gender = Text(patient, "gender");
        var birthDate = Text(patient, "birthDate");
        return new PatientInfo(
            Text(identifier, "value") ?? throw new InvalidMessageException("Patient.identifier.value is missing"),
            Text(identifier, "system") ?? throw new InvalidMessageException("Patient.identifier.system is missing"),
            birthDate is null ? null
                : DateOnly.TryParseExact(birthDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed
                : throw new InvalidMessageException("Patient.birthDate is not a full date"),
            gender switch
            {
                null => null,
                "female" => Sex.Female,
                "male" => Sex.Male,
                "other" => Sex.Other,
                _ => Sex.Unknown,
            },
            gender);
    }

    private static VisitInfo MapVisit(string encounterId, JsonElement encounter)
    {
        // R4: Encounter.class is a single Coding (v3 ActCode).
        var classCode = (encounter.TryGetProperty("class", out var coding) ? Text(coding, "code") : null)
            ?? throw new InvalidMessageException("Encounter.class.code is missing");
        var visitClass = classCode switch
        {
            "IMP" => VisitClass.Inpatient,
            "AMB" => VisitClass.Outpatient,
            "EMER" => VisitClass.Emergency,
            _ => throw new InvalidMessageException("Encounter.class is not supported"),
        };
        var period = encounter.TryGetProperty("period", out var p) ? p : default;
        return new VisitInfo(
            encounterId,
            visitClass,
            classCode,
            ParseDateTime(Text(period, "start"), "Encounter.period.start")
                ?? throw new InvalidMessageException("Encounter.period.start is missing"),
            ParseDateTime(Text(period, "end"), "Encounter.period.end"));
    }

    private static Diagnosis MapCondition(JsonElement condition)
    {
        var coding = condition.TryGetProperty("code", out var code) ? First(code, "coding") : default;
        var system = Text(coding, "system") ?? throw new InvalidMessageException("Condition.code.coding.system is missing");
        var onset = ParseDateTime(Text(condition, "onsetDateTime"), "Condition.onsetDateTime");
        return new Diagnosis(
            Text(coding, "code") ?? throw new InvalidMessageException("Condition.code.coding.code is missing"),
            Normalization.CodeSystem(system),
            system,
            Text(coding, "display"),
            onset is null ? null : DateOnly.FromDateTime(onset.Value));
    }

    /// <summary>The id in a reference like "Encounter/enc-1", looked up in the first property that has one.</summary>
    private static string? ReferencedId(JsonElement resource, string type, params string[] properties)
    {
        var prefix = type + "/";
        foreach (var property in properties)
        {
            if (resource.TryGetProperty(property, out var reference)
                && Text(reference, "reference") is { } value
                && value.StartsWith(prefix, StringComparison.Ordinal))
            {
                return value[prefix.Length..];
            }
        }

        return null;
    }

    /// <summary>FHIR datetimes carry an offset; the canonical event keeps the local wall time it describes.</summary>
    private static DateTime? ParseDateTime(string? value, string location) =>
        value is null ? null
        : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed.DateTime
        : throw new InvalidMessageException($"{location} is not a valid datetime");

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonElement First(JsonElement element, string arrayProperty) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(arrayProperty, out var array)
        && array.ValueKind == JsonValueKind.Array
        && array.GetArrayLength() > 0
            ? array[0]
            : default;

    private static string Minify(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, MinifiedJson))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>A rejected encounter group as a small Bundle of its own, so it can be fixed and replayed as is.</summary>
    private static string GroupAsBundle(JsonElement bundle, Entry encounter, Dictionary<string, Entry> patients, List<Entry> linked)
    {
        var resources = new List<Entry>();
        if (ReferencedId(encounter.Resource, "Patient", "subject") is { } patientId && patients.TryGetValue(patientId, out var patient))
        {
            resources.Add(patient);
        }

        resources.Add(encounter);
        resources.AddRange(linked);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = MinifiedJson.Encoder, Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("resourceType", "Bundle");
            writer.WriteString("id", Text(bundle, "id"));
            writer.WriteString("type", "collection");
            writer.WriteString("timestamp", Text(bundle, "timestamp"));
            writer.WriteStartArray("entry");
            foreach (var entry in resources)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("resource");
                entry.Resource.WriteTo(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

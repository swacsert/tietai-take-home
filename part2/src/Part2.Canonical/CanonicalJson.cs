using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Part2.Canonical;

/// <summary>
/// Defines the JSON shape of the contract in one place, so the ingestor (which writes it) and the
/// writer (which reads it) cannot disagree on field names, enum spelling or strictness.
/// </summary>
public static class CanonicalJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>Serializes one event as a single JSONL line, without the trailing newline.</summary>
    public static string Serialize(CanonicalEvent canonicalEvent)
    {
        return JsonSerializer.Serialize(canonicalEvent, Options);
    }

    /// <summary>Parses one JSONL line. Throws JsonException if the line breaks the contract.</summary>
    public static CanonicalEvent Deserialize(string line)
    {
        CanonicalEvent? canonicalEvent = JsonSerializer.Deserialize<CanonicalEvent>(line, Options);
        if (canonicalEvent == null)
        {
            throw new JsonException("Line is the JSON literal null, not an event.");
        }

        if (canonicalEvent.SchemaVersion != CanonicalEvent.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported schema_version {canonicalEvent.SchemaVersion}; this build reads {CanonicalEvent.CurrentSchemaVersion}.");
        }

        return canonicalEvent;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,

            // Strict reading: a renamed, missing or wrongly null field fails here, at the boundary,
            // instead of turning into a silent null on the other side.
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,

            // The JSONL is a file on disk, never embedded in HTML, so HTML-safe escaping would only make
            // raw HL7 such as "^~\&" harder to read. Control characters are still escaped.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }
}

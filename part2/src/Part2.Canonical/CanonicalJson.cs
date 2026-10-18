using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Part2.Canonical;

/// <summary>
/// The single place where the JSON shape of the contract is defined, so the producer
/// and the consumer cannot disagree on field names, enum spelling or strictness.
/// </summary>
public static class CanonicalJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
        // Strict reader: a renamed, missing or null-where-not-allowed field fails loudly
        // instead of turning into a silent null on the other side of the boundary.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        // The JSONL is a file on disk, never embedded in HTML, so HTML-safe escaping
        // would only make raw HL7 (e.g. "^~\&") harder to read. Control bytes are still escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Serializes one event as a single JSONL line (no trailing newline).</summary>
    public static string Serialize(CanonicalEvent canonicalEvent) =>
        JsonSerializer.Serialize(canonicalEvent, Options);

    /// <summary>Parses one JSONL line; throws <see cref="JsonException"/> if it breaks the contract.</summary>
    public static CanonicalEvent Deserialize(string line)
    {
        var canonicalEvent = JsonSerializer.Deserialize<CanonicalEvent>(line, Options)
            ?? throw new JsonException("Line is the JSON literal null, not an event.");

        if (canonicalEvent.SchemaVersion != CanonicalEvent.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported schema_version {canonicalEvent.SchemaVersion}; this build reads {CanonicalEvent.CurrentSchemaVersion}.");
        }

        return canonicalEvent;
    }
}

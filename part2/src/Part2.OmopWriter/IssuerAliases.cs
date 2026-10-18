using System.Text.Json;

namespace Part2.OmopWriter;

/// <summary>
/// Which identifier issuers are the same hospital (config/issuers.json). HL7 says "SAN_ANDRES" and
/// FHIR says an OID; with this mapping both land on one person row.
/// </summary>
internal sealed class IssuerAliases(IReadOnlyDictionary<string, string> aliases)
{
    public string Canonicalize(string issuer) => aliases.GetValueOrDefault(issuer, issuer);

    public static IssuerAliases Load(string path)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var aliases = document.RootElement.GetProperty("aliases")
            .EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()
                ?? throw new InvalidDataException($"{path}: alias for '{p.Name}' is not a string"));
        return new IssuerAliases(aliases);
    }
}

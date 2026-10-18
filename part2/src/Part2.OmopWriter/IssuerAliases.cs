using System.Text.Json;

namespace Part2.OmopWriter;

/// <summary>
/// Says which patient-identifier issuers are the same hospital (config/issuers.json). HL7 sends
/// "SAN_ANDRES" and FHIR sends an OID; with this mapping both land on one person row. (D24)
/// </summary>
internal sealed class IssuerAliases
{
    private readonly IReadOnlyDictionary<string, string> _aliases;

    public IssuerAliases(IReadOnlyDictionary<string, string> aliases)
    {
        _aliases = aliases;
    }

    /// <summary>The issuer to use in person_source_value: its alias if it has one, otherwise the issuer itself.</summary>
    public string Canonicalize(string issuer)
    {
        if (_aliases.TryGetValue(issuer, out string? canonical))
        {
            return canonical;
        }

        return issuer;
    }

    /// <summary>Reads the "aliases" object of the config file. Comments are allowed in the file.</summary>
    public static IssuerAliases Load(string path)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip };
        var aliases = new Dictionary<string, string>();

        using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), options))
        {
            foreach (JsonProperty alias in document.RootElement.GetProperty("aliases").EnumerateObject())
            {
                string? canonical = alias.Value.GetString();
                if (canonical == null)
                {
                    throw new InvalidDataException($"{path}: alias for '{alias.Name}' is not a string");
                }

                aliases.Add(alias.Name, canonical);
            }
        }

        return new IssuerAliases(aliases);
    }
}

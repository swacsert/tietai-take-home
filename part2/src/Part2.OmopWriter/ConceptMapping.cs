namespace Part2.OmopWriter;

/// <summary>The OMOP standard concept a source code "Maps to", and the domain that concept belongs to.</summary>
internal sealed record ConceptMapping(int ConceptId, string DomainId);

/// <summary>
/// Source code → standard concept. Today it reads a CSV stub; in production it would query the
/// OMOP vocabulary tables (concept_relationship "Maps to"). The loader only sees this interface.
/// </summary>
internal interface IConceptMapper
{
    /// <summary>The mapping, or null when the vocabulary has no standard concept for the code.</summary>
    ConceptMapping? Map(string vocabularyId, string sourceCode);
}

/// <summary>Reads data/vocab_icd10.csv (header: source_code, source_vocabulary_id, …, target_concept_id, …, domain_id).</summary>
internal sealed class CsvConceptMapper(IReadOnlyDictionary<(string Vocabulary, string Code), ConceptMapping> mappings)
    : IConceptMapper
{
    public ConceptMapping? Map(string vocabularyId, string sourceCode) =>
        mappings.GetValueOrDefault((vocabularyId, sourceCode));

    public static CsvConceptMapper Load(string path)
    {
        var lines = File.ReadAllLines(path);
        var header = ParseLine(lines[0]);
        int Column(string name) => header.IndexOf(name) is var index and >= 0
            ? index
            : throw new InvalidDataException($"{path}: column '{name}' is missing");
        var (code, vocabulary, concept, domain) =
            (Column("source_code"), Column("source_vocabulary_id"), Column("target_concept_id"), Column("domain_id"));

        var mappings = new Dictionary<(string, string), ConceptMapping>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
            {
                continue;
            }

            var fields = ParseLine(lines[i]);
            var key = (fields[vocabulary], fields[code]);
            // The stub is one-to-one. A real "Maps to" can be one-to-many; that needs a design change, not a silent pick.
            if (!mappings.TryAdd(key, new ConceptMapping(int.Parse(fields[concept]), fields[domain])))
            {
                throw new InvalidDataException($"{path} line {i + 1}: second mapping for {key}");
            }
        }

        return new CsvConceptMapper(mappings);
    }

    /// <summary>Splits one CSV line; handles quoted fields with commas and "" escapes.</summary>
    internal static List<string> ParseLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted && c == '"' && i + 1 < line.Length && line[i + 1] == '"')
            {
                current.Append('"');
                i++;
            }
            else if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}

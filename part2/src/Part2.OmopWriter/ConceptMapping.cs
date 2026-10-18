using System.Text;

namespace Part2.OmopWriter;

/// <summary>The OMOP standard concept a source code "Maps to", and the domain that concept belongs to.</summary>
internal sealed record ConceptMapping(int ConceptId, string DomainId);

/// <summary>
/// Maps a source code to a standard concept. Today the implementation reads a CSV stub; in production
/// it would query the OMOP vocabulary tables (concept_relationship "Maps to"). The loader only sees
/// this interface. (D10)
/// </summary>
internal interface IConceptMapper
{
    /// <summary>The mapping, or null when the vocabulary has no standard concept for the code.</summary>
    ConceptMapping? Map(string vocabularyId, string sourceCode);
}

/// <summary>Reads data/vocab_icd10.csv (columns used: source_code, source_vocabulary_id, target_concept_id, domain_id).</summary>
internal sealed class CsvConceptMapper : IConceptMapper
{
    private readonly IReadOnlyDictionary<(string Vocabulary, string Code), ConceptMapping> _mappings;

    public CsvConceptMapper(IReadOnlyDictionary<(string Vocabulary, string Code), ConceptMapping> mappings)
    {
        _mappings = mappings;
    }

    public ConceptMapping? Map(string vocabularyId, string sourceCode)
    {
        if (_mappings.TryGetValue((vocabularyId, sourceCode), out ConceptMapping? mapping))
        {
            return mapping;
        }

        return null;
    }

    public static CsvConceptMapper Load(string path)
    {
        string[] lines = File.ReadAllLines(path);
        List<string> header = ParseLine(lines[0]);
        int codeColumn = FindColumn(header, "source_code", path);
        int vocabularyColumn = FindColumn(header, "source_vocabulary_id", path);
        int conceptColumn = FindColumn(header, "target_concept_id", path);
        int domainColumn = FindColumn(header, "domain_id", path);

        var mappings = new Dictionary<(string Vocabulary, string Code), ConceptMapping>();
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
            {
                continue;
            }

            List<string> fields = ParseLine(lines[i]);
            var key = (fields[vocabularyColumn], fields[codeColumn]);
            var mapping = new ConceptMapping(int.Parse(fields[conceptColumn]), fields[domainColumn]);

            // The stub is one-to-one. A real "Maps to" can be one-to-many; that needs a design decision,
            // not a silent pick of one row.
            if (!mappings.TryAdd(key, mapping))
            {
                throw new InvalidDataException($"{path} line {i + 1}: second mapping for {key}");
            }
        }

        return new CsvConceptMapper(mappings);
    }

    private static int FindColumn(List<string> header, string name, string path)
    {
        int index = header.IndexOf(name);
        if (index < 0)
        {
            throw new InvalidDataException($"{path}: column '{name}' is missing");
        }

        return index;
    }

    /// <summary>Splits one CSV line. Handles quoted fields that contain commas, and "" inside quotes.</summary>
    internal static List<string> ParseLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool insideQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            bool isEscapedQuote = insideQuotes && c == '"' && i + 1 < line.Length && line[i + 1] == '"';

            if (isEscapedQuote)
            {
                current.Append('"');
                i++;
            }
            else if (c == '"')
            {
                insideQuotes = !insideQuotes;
            }
            else if (c == ',' && !insideQuotes)
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

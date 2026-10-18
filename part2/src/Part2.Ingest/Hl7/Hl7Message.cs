namespace Part2.Ingest.Hl7;

/// <summary>
/// One HL7 v2 message, split with the separators the message itself declares in MSH-1 and MSH-2.
/// No separator is hard-coded: a message that declares '#' and '@' is read with '#' and '@'.
/// </summary>
internal sealed class Hl7Message
{
    private Hl7Message(char fieldSeparator, string encodingCharacters, IReadOnlyList<Hl7Segment> segments)
    {
        FieldSeparator = fieldSeparator;
        EncodingCharacters = encodingCharacters;
        Segments = segments;
    }

    /// <summary>MSH-1.</summary>
    public char FieldSeparator { get; }

    /// <summary>MSH-2: component, repetition, escape and subcomponent separators, in that order.</summary>
    public string EncodingCharacters { get; }

    public char ComponentSeparator => EncodingCharacters[0];

    public IReadOnlyList<Hl7Segment> Segments { get; }

    /// <summary>Parses the lines of one message; throws <see cref="InvalidMessageException"/> if it is not HL7.</summary>
    public static Hl7Message Parse(IReadOnlyList<string> lines)
    {
        var header = lines[0];
        if (!header.StartsWith("MSH", StringComparison.Ordinal) || header.Length < 8)
        {
            throw new InvalidMessageException("first segment is not MSH");
        }

        var fieldSeparator = header[3];
        var encodingEnd = header.IndexOf(fieldSeparator, 4);
        var encodingCharacters = encodingEnd < 0 ? header[4..] : header[4..encodingEnd];

        // HL7 v2.5 declares four encoding characters (v2.7 adds a fifth); all must be distinct
        // punctuation, otherwise splitting would be ambiguous.
        var usable = IsSeparator(fieldSeparator)
            && encodingCharacters.Length is 4 or 5
            && encodingCharacters.All(IsSeparator)
            && encodingCharacters.Distinct().Count() == encodingCharacters.Length
            && !encodingCharacters.Contains(fieldSeparator);
        if (!usable)
        {
            throw new InvalidMessageException("MSH-1/MSH-2 do not declare usable separators");
        }

        var segments = new List<Hl7Segment>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            if (!HasSegmentName(lines[i], fieldSeparator))
            {
                throw new InvalidMessageException($"line {i + 1} of the message is not a valid segment");
            }

            segments.Add(new Hl7Segment(lines[i], fieldSeparator, encodingCharacters[0], encodingCharacters[1]));
        }

        return new Hl7Message(fieldSeparator, encodingCharacters, segments);
    }

    private static bool IsSeparator(char c) => char.IsPunctuation(c) || char.IsSymbol(c);

    // Three characters, uppercase letter first, then the field separator or the end of the line.
    private static bool HasSegmentName(string line, char fieldSeparator) =>
        line.Length >= 3
        && char.IsAsciiLetterUpper(line[0])
        && (char.IsAsciiLetterUpper(line[1]) || char.IsAsciiDigit(line[1]))
        && (char.IsAsciiLetterUpper(line[2]) || char.IsAsciiDigit(line[2]))
        && (line.Length == 3 || line[3] == fieldSeparator);
}

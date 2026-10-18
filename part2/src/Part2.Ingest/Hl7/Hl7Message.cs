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

    /// <summary>Parses the lines of one message. Throws InvalidMessageException if it is not HL7.</summary>
    public static Hl7Message Parse(IReadOnlyList<string> lines)
    {
        string header = lines[0];
        if (!header.StartsWith("MSH", StringComparison.Ordinal) || header.Length < 8)
        {
            throw new InvalidMessageException("first segment is not MSH");
        }

        char fieldSeparator = header[3];
        string encodingCharacters = ReadEncodingCharacters(header, fieldSeparator);
        if (!AreUsableSeparators(fieldSeparator, encodingCharacters))
        {
            throw new InvalidMessageException("MSH-1/MSH-2 do not declare usable separators");
        }

        var segments = new List<Hl7Segment>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (!StartsWithSegmentName(lines[i], fieldSeparator))
            {
                throw new InvalidMessageException($"line {i + 1} of the message is not a valid segment");
            }

            segments.Add(new Hl7Segment(lines[i], fieldSeparator, encodingCharacters[0], encodingCharacters[1]));
        }

        return new Hl7Message(fieldSeparator, encodingCharacters, segments);
    }

    // MSH-2 runs from just after the field separator up to the next field separator.
    private static string ReadEncodingCharacters(string header, char fieldSeparator)
    {
        int end = header.IndexOf(fieldSeparator, 4);
        if (end < 0)
        {
            return header.Substring(4);
        }

        return header.Substring(4, end - 4);
    }

    // HL7 v2.5 declares four encoding characters (v2.7 adds a fifth). They must all be punctuation,
    // all different and different from the field separator, otherwise splitting would be ambiguous.
    private static bool AreUsableSeparators(char fieldSeparator, string encodingCharacters)
    {
        if (!IsSeparator(fieldSeparator))
        {
            return false;
        }

        if (encodingCharacters.Length != 4 && encodingCharacters.Length != 5)
        {
            return false;
        }

        if (encodingCharacters.Contains(fieldSeparator) || encodingCharacters.Distinct().Count() != encodingCharacters.Length)
        {
            return false;
        }

        foreach (char c in encodingCharacters)
        {
            if (!IsSeparator(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSeparator(char c)
    {
        return char.IsPunctuation(c) || char.IsSymbol(c);
    }

    // A segment starts with a three-character name (uppercase letter, then uppercase letters or digits),
    // followed by the field separator or by the end of the line.
    private static bool StartsWithSegmentName(string line, char fieldSeparator)
    {
        if (line.Length < 3)
        {
            return false;
        }

        if (!char.IsAsciiLetterUpper(line[0]) || !IsUpperOrDigit(line[1]) || !IsUpperOrDigit(line[2]))
        {
            return false;
        }

        return line.Length == 3 || line[3] == fieldSeparator;
    }

    private static bool IsUpperOrDigit(char c)
    {
        return char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c);
    }
}

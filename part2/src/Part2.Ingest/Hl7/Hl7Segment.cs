namespace Part2.Ingest.Hl7;

/// <summary>
/// One HL7 segment. Field numbers follow the HL7 convention: in MSH the field separator itself is
/// MSH-1, so MSH-2 is the first value after it; in every other segment field 1 follows the name.
/// </summary>
internal sealed class Hl7Segment
{
    private readonly string[] _fields;
    private readonly char _fieldSeparator;
    private readonly char _componentSeparator;
    private readonly char _repetitionSeparator;

    public Hl7Segment(string raw, char fieldSeparator, char componentSeparator, char repetitionSeparator)
    {
        Raw = raw;
        _fields = raw.Split(fieldSeparator);
        _fieldSeparator = fieldSeparator;
        _componentSeparator = componentSeparator;
        _repetitionSeparator = repetitionSeparator;
    }

    public string Raw { get; }

    public string Name => _fields[0];

    /// <summary>Highest field number present in the segment.</summary>
    public int FieldCount => _fields.Length - 1 + Offset;

    // In MSH, array index 1 holds MSH-2, so field n lives at index n - 1.
    private int Offset => Name == "MSH" ? 1 : 0;

    /// <summary>Field <paramref name="number"/> as received, or "" when the segment is shorter.</summary>
    public string Field(int number)
    {
        if (Name == "MSH" && number == 1)
        {
            return _fieldSeparator.ToString();
        }

        var index = number - Offset;
        return index >= 1 && index < _fields.Length ? _fields[index] : "";
    }

    /// <summary>Component <paramref name="component"/> (from 1) of the first repetition of a field.</summary>
    public string Component(int field, int component)
    {
        var firstRepetition = Field(field).Split(_repetitionSeparator)[0];
        var components = firstRepetition.Split(_componentSeparator);
        return component <= components.Length ? components[component - 1] : "";
    }

    /// <summary>The raw segment with one field's value emptied and the field itself kept.</summary>
    public string RawWithFieldEmptied(int number)
    {
        var fields = (string[])_fields.Clone();
        var index = number - Offset;
        if (index >= 1 && index < fields.Length)
        {
            fields[index] = "";
        }

        return string.Join(_fieldSeparator, fields);
    }
}

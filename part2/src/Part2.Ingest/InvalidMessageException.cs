namespace Part2.Ingest;

/// <summary>
/// Thrown when a source unit (an HL7 message or a FHIR Encounter) cannot become a canonical event.
/// The message text is the reason written next to the unit in the DLQ.
/// </summary>
internal sealed class InvalidMessageException : Exception
{
    public InvalidMessageException(string reason)
        : base(reason)
    {
    }
}

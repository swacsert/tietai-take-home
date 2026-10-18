namespace Part2.Ingest;

/// <summary>
/// The source unit (an HL7 message or a FHIR encounter group) cannot become a canonical event.
/// The message text is the reason written next to it in the DLQ.
/// </summary>
internal sealed class InvalidMessageException(string reason) : Exception(reason);

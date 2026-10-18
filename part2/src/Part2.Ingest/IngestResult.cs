using Part2.Canonical;

namespace Part2.Ingest;

/// <summary>A source unit that went to the DLQ: its position in the input file, its raw text and why.</summary>
internal sealed record DeadLetter(int Position, string Raw, string Reason);

/// <summary>Everything one input file produced. <paramref name="UnlinkedResources"/> counts, by type, FHIR resources that hang from no Encounter.</summary>
internal sealed record IngestResult(
    IReadOnlyList<CanonicalEvent> Events,
    IReadOnlyList<DeadLetter> DeadLetters,
    IReadOnlyDictionary<string, int> UnlinkedResources);

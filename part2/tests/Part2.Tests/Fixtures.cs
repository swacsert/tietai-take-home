using Part2.Ingest;
using Part2.Ingest.Fhir;
using Part2.Ingest.Hl7;

namespace Part2.Tests;

/// <summary>Access to the real sample files in part2/data, copied next to the test assembly.</summary>
internal static class Fixtures
{
    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    public static string Read(string name) => File.ReadAllText(PathOf(name));

    public static string[] ExampleLines =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "canonical-event.example.jsonl"));

    public static IngestResult IngestHl7() => Hl7Feed.Ingest(Read("adt_feed.hl7"), "adt_feed.hl7");

    public static IngestResult IngestFhir() => FhirBundleMapper.Map(Read("fhir_bundle.json"), "fhir_bundle.json");
}

using Part2.Ingest;
using Part2.Ingest.Fhir;
using Part2.Ingest.Hl7;

// Usage: Part2.Ingest [--out DIR] FILE...   (.hl7 = HL7 v2 batch, .json = FHIR R4 Bundle)
var outDirectory = "out";
var inputs = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--out" && i + 1 < args.Length)
    {
        outDirectory = args[++i];
    }
    else
    {
        inputs.Add(args[i]);
    }
}

if (inputs.Count == 0)
{
    Console.Error.WriteLine("usage: Part2.Ingest [--out DIR] FILE...");
    return 2;
}

foreach (var input in inputs)
{
    if (!File.Exists(input))
    {
        Console.Error.WriteLine($"input not found: {input}");
        return 2;
    }

    var fileName = Path.GetFileName(input);
    var text = File.ReadAllText(input);
    IngestResult? result = Path.GetExtension(input).ToLowerInvariant() switch
    {
        ".hl7" => Hl7Feed.Ingest(text, fileName),
        ".json" => FhirBundleMapper.Map(text, fileName),
        _ => null,
    };
    if (result is null)
    {
        Console.Error.WriteLine($"unsupported input type: {input}");
        return 2;
    }

    OutputWriter.Write(result, outDirectory, fileName);
    Console.WriteLine($"{fileName}: {result.Events.Count} events, {result.DeadLetters.Count} to DLQ");
    foreach (var deadLetter in result.DeadLetters)
    {
        Console.WriteLine($"  DLQ #{deadLetter.Position}: {deadLetter.Reason}");
    }

    foreach (var (type, count) in result.UnlinkedResources)
    {
        Console.WriteLine($"  not linked to any Encounter (counted, not loaded): {count} {type}");
    }
}

return 0;

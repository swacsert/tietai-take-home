using Part2.Ingest.Fhir;
using Part2.Ingest.Hl7;

namespace Part2.Ingest;

/// <summary>
/// Command line: Part2.Ingest [--out DIR] FILE...
/// A .hl7 file is read as an HL7 v2 batch and a .json file as a FHIR R4 Bundle.
/// </summary>
internal static class Program
{
    private const int Success = 0;
    private const int UsageError = 2;

    private static int Main(string[] args)
    {
        string outDirectory = "out";
        var inputs = new List<string>();
        for (int i = 0; i < args.Length; i++)
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
            return UsageError;
        }

        foreach (string input in inputs)
        {
            if (!IngestFile(input, outDirectory))
            {
                return UsageError;
            }
        }

        return Success;
    }

    // Returns false when the input cannot be processed at all: the file is missing or of an unknown type.
    // Bad messages inside a good file are not an error here; they go to the DLQ.
    private static bool IngestFile(string input, string outDirectory)
    {
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"input not found: {input}");
            return false;
        }

        string fileName = Path.GetFileName(input);
        string text = File.ReadAllText(input);
        IngestResult? result = ReadSourceFile(text, fileName);
        if (result == null)
        {
            Console.Error.WriteLine($"unsupported input type: {input}");
            return false;
        }

        OutputWriter.Write(result, outDirectory, fileName);
        PrintSummary(fileName, result);
        return true;
    }

    private static IngestResult? ReadSourceFile(string text, string fileName)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension == ".hl7")
        {
            return Hl7Feed.Ingest(text, fileName);
        }
        else if (extension == ".json")
        {
            return FhirBundleMapper.Map(text, fileName);
        }

        return null;
    }

    // Only counts, file names, positions and reasons: never message content. (D30)
    private static void PrintSummary(string fileName, IngestResult result)
    {
        Console.WriteLine($"{fileName}: {result.Events.Count} events, {result.DeadLetters.Count} to DLQ");
        foreach (DeadLetter deadLetter in result.DeadLetters)
        {
            Console.WriteLine($"  DLQ {fileName} position {deadLetter.Position}: {deadLetter.Reason}");
        }

        foreach (KeyValuePair<string, int> unlinked in result.UnlinkedResources)
        {
            Console.WriteLine($"  not linked to any Encounter (counted, not loaded): {unlinked.Value} {unlinked.Key}");
        }
    }
}

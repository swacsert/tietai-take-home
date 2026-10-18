using Part2.Canonical;

namespace Part2.Ingest;

/// <summary>
/// Writes one input file's result: out/canonical/{stem}.jsonl, and for each dead letter
/// out/dlq/{stem}.{position}{extension} (the raw unit, replayable) plus a .reason.txt next to it.
/// </summary>
internal static class OutputWriter
{
    public static void Write(IngestResult result, string outDirectory, string inputFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(inputFileName);
        var extension = Path.GetExtension(inputFileName);
        var canonicalDirectory = Directory.CreateDirectory(Path.Combine(outDirectory, "canonical")).FullName;
        var dlqDirectory = Directory.CreateDirectory(Path.Combine(outDirectory, "dlq")).FullName;

        // The contract fixes \n as line terminator, whatever the platform.
        File.WriteAllText(
            Path.Combine(canonicalDirectory, stem + ".jsonl"),
            string.Concat(result.Events.Select(e => CanonicalJson.Serialize(e) + "\n")));

        // A rerun rewrites this input's DLQ entries, so a message that is now fixed does not linger.
        foreach (var stale in Directory.EnumerateFiles(dlqDirectory, stem + ".*"))
        {
            File.Delete(stale);
        }

        foreach (var deadLetter in result.DeadLetters)
        {
            var baseName = Path.Combine(dlqDirectory, $"{stem}.{deadLetter.Position:D4}");
            File.WriteAllText(baseName + extension, deadLetter.Raw + "\n");
            File.WriteAllText(baseName + ".reason.txt", deadLetter.Reason + "\n");
        }
    }
}

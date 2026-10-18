using Part2.Canonical;

namespace Part2.Ingest.Hl7;

/// <summary>Reads a batch of HL7 messages; one bad message goes to the DLQ and the batch goes on.</summary>
internal static class Hl7Feed
{
    public static IngestResult Ingest(string text, string fileName)
    {
        var events = new List<CanonicalEvent>();
        var deadLetters = new List<DeadLetter>();
        var position = 0;

        foreach (var lines in SplitMessages(text))
        {
            position++;
            try
            {
                events.Add(Hl7EventMapper.Map(Hl7Message.Parse(lines), fileName, position));
            }
            catch (InvalidMessageException ex)
            {
                deadLetters.Add(new DeadLetter(position, string.Join('\n', lines), ex.Message));
            }
            catch (Exception ex)
            {
                // A bug triggered by one untrusted message must not stop the rest of the batch.
                deadLetters.Add(new DeadLetter(position, string.Join('\n', lines), $"unexpected error: {ex.Message}"));
            }
        }

        return new IngestResult(events, deadLetters, new Dictionary<string, int>());
    }

    /// <summary>
    /// Segments end with \r (the HL7 standard), \n or \r\n. A message ends at a blank line or where
    /// the next MSH starts.
    /// </summary>
    internal static IEnumerable<IReadOnlyList<string>> SplitMessages(string text)
    {
        var current = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (current.Count > 0 && (line.Length == 0 || line.StartsWith("MSH", StringComparison.Ordinal)))
            {
                yield return current;
                current = [];
            }

            if (line.Length > 0)
            {
                current.Add(line);
            }
        }

        if (current.Count > 0)
        {
            yield return current;
        }
    }
}

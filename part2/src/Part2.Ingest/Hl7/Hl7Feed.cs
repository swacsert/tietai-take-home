using Part2.Canonical;

namespace Part2.Ingest.Hl7;

/// <summary>Reads a batch of HL7 messages. A bad message goes to the DLQ and the batch goes on.</summary>
internal static class Hl7Feed
{
    public static IngestResult Ingest(string text, string fileName)
    {
        var events = new List<CanonicalEvent>();
        var deadLetters = new List<DeadLetter>();
        int position = 0;

        foreach (IReadOnlyList<string> lines in SplitMessages(text))
        {
            position++;
            string raw = string.Join('\n', lines);
            try
            {
                Hl7Message message = Hl7Message.Parse(lines);
                events.Add(Hl7EventMapper.Map(message, fileName, position));
            }
            catch (InvalidMessageException ex)
            {
                deadLetters.Add(new DeadLetter(position, raw, ex.Message));
            }
            catch (Exception ex)
            {
                // Deliberately broad. The feed is untrusted: if one message hits a bug in this parser,
                // that message goes to the DLQ and the other messages are still processed. The exception
                // type in the reason points at the bug; its message is left out because it may quote the input.
                deadLetters.Add(new DeadLetter(position, raw, $"unexpected error ({ex.GetType().Name})"));
            }
        }

        return new IngestResult(events, deadLetters, new Dictionary<string, int>());
    }

    /// <summary>
    /// Splits the feed into messages. Segments end with \r (the HL7 standard), \n or \r\n.
    /// A message ends at a blank line or where the next MSH starts.
    /// </summary>
    internal static IEnumerable<IReadOnlyList<string>> SplitMessages(string text)
    {
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var current = new List<string>();

        foreach (string line in normalized.Split('\n'))
        {
            bool messageEnds = line.Length == 0 || line.StartsWith("MSH", StringComparison.Ordinal);
            if (messageEnds && current.Count > 0)
            {
                yield return current;
                current = new List<string>();
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

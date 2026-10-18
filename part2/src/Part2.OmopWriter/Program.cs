using Microsoft.Data.Sqlite;

namespace Part2.OmopWriter;

/// <summary>
/// Command line: Part2.OmopWriter [--db FILE] [--vocab CSV] [--issuers JSON] [JSONL file or directory...]
/// With no input, it reads every .jsonl file in out/canonical.
/// </summary>
internal static class Program
{
    private const int Success = 0;
    private const int UsageError = 2;

    private static int Main(string[] args)
    {
        string databasePath = "out/omop.db";
        string vocabularyPath = "data/vocab_icd10.csv";
        string issuersPath = "config/issuers.json";
        var inputs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            bool hasValue = i + 1 < args.Length;
            if (args[i] == "--db" && hasValue)
            {
                databasePath = args[++i];
            }
            else if (args[i] == "--vocab" && hasValue)
            {
                vocabularyPath = args[++i];
            }
            else if (args[i] == "--issuers" && hasValue)
            {
                issuersPath = args[++i];
            }
            else
            {
                inputs.Add(args[i]);
            }
        }

        if (inputs.Count == 0)
        {
            inputs.Add("out/canonical");
        }

        List<string>? files = FindJsonlFiles(inputs);
        if (files == null)
        {
            return UsageError;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        using (SqliteConnection connection = OmopDatabase.Open(databasePath))
        {
            var loader = new OmopLoader(connection, CsvConceptMapper.Load(vocabularyPath), IssuerAliases.Load(issuersPath));
            LoadReport report = loader.Load(ReadLines(files));
            PrintReport(databasePath, files.Count, report, connection);
        }

        return Success;
    }

    // A directory contributes its .jsonl files in name order. Returns null (after an error message)
    // if an input does not exist.
    private static List<string>? FindJsonlFiles(List<string> inputs)
    {
        var files = new List<string>();
        foreach (string input in inputs)
        {
            if (Directory.Exists(input))
            {
                string[] found = Directory.GetFiles(input, "*.jsonl");
                Array.Sort(found, StringComparer.Ordinal);
                files.AddRange(found);
            }
            else if (File.Exists(input))
            {
                files.Add(input);
            }
            else
            {
                Console.Error.WriteLine($"input not found: {input}");
                return null;
            }
        }

        return files;
    }

    // Reads the files line by line, skipping blank lines. The files are only read, never moved or
    // deleted, so running the writer again replays them. (D6)
    private static IEnumerable<SourceLine> ReadLines(List<string> files)
    {
        foreach (string file in files)
        {
            int number = 0;
            foreach (string text in File.ReadLines(file))
            {
                number++;
                if (text.Length > 0)
                {
                    yield return new SourceLine(Path.GetFileName(file), number, text);
                }
            }
        }
    }

    private static void PrintReport(string databasePath, int fileCount, LoadReport report, SqliteConnection connection)
    {
        Console.WriteLine($"read {fileCount} file(s) into {databasePath}");
        Console.WriteLine($"  events loaded: {report.Loaded}, already loaded (rerun or retransmission): {report.AlreadyLoaded}");
        Console.WriteLine($"  quarantined events: {report.QuarantinedEvents}, conflicts: {report.Conflicts}, invalid lines: {report.InvalidLines}");
        foreach (KeyValuePair<string, int> outOfScope in report.OutOfScopeByDomain)
        {
            Console.WriteLine($"  domain {outOfScope.Key}: table out of scope, {outOfScope.Value} diagnosis(es) not loaded");
        }

        foreach (string table in OmopDatabase.Tables)
        {
            Console.WriteLine($"  {table}: {OmopDatabase.CountRows(connection, table)} rows");
        }
    }
}

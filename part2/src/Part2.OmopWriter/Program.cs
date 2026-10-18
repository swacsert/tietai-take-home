using Part2.OmopWriter;

// Usage: Part2.OmopWriter [--db FILE] [--vocab CSV] [--issuers JSON] [JSONL file or directory...]
var databasePath = "out/omop.db";
var vocabularyPath = "data/vocab_icd10.csv";
var issuersPath = "config/issuers.json";
var inputs = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--db" when i + 1 < args.Length: databasePath = args[++i]; break;
        case "--vocab" when i + 1 < args.Length: vocabularyPath = args[++i]; break;
        case "--issuers" when i + 1 < args.Length: issuersPath = args[++i]; break;
        default: inputs.Add(args[i]); break;
    }
}

if (inputs.Count == 0)
{
    inputs.Add("out/canonical");
}

var files = new List<string>();
foreach (var input in inputs)
{
    if (Directory.Exists(input))
    {
        files.AddRange(Directory.GetFiles(input, "*.jsonl").Order(StringComparer.Ordinal));
    }
    else if (File.Exists(input))
    {
        files.Add(input);
    }
    else
    {
        Console.Error.WriteLine($"input not found: {input}");
        return 2;
    }
}

// The JSONL files are only read, never moved or deleted: rerunning replays them (D6).
var lines = files
    .SelectMany(file => File.ReadLines(file).Select((text, index) => new SourceLine(Path.GetFileName(file), index + 1, text)))
    .Where(line => line.Text.Length > 0);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
using var connection = OmopDatabase.Open(databasePath);
var loader = new OmopLoader(connection, CsvConceptMapper.Load(vocabularyPath), IssuerAliases.Load(issuersPath));
var report = loader.Load(lines);

Console.WriteLine($"read {files.Count} file(s) into {databasePath}");
Console.WriteLine($"  events loaded: {report.Loaded}, already loaded (rerun or retransmission): {report.AlreadyLoaded}");
Console.WriteLine($"  quarantined events: {report.QuarantinedEvents}, conflicts: {report.Conflicts}, invalid lines: {report.InvalidLines}");
Console.WriteLine($"  quarantined diagnoses: {report.QuarantinedDiagnoses}, superseded diagnosis sets: {report.SupersededDiagnosisSets}");
foreach (var table in OmopDatabase.Tables)
{
    Console.WriteLine($"  {table}: {OmopDatabase.CountRows(connection, table)} rows");
}

return 0;

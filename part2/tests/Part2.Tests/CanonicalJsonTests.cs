using System.Text.Json;
using Part2.Canonical;

namespace Part2.Tests;

public class CanonicalJsonTests
{
    private static readonly string[] ExampleLines =
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "canonical-event.example.jsonl"));

    private static string Hl7Example => ExampleLines[0];

    [Fact]
    public void Documented_examples_round_trip_byte_for_byte()
    {
        // If docs/canonical-event.example.jsonl and the C# types drift apart, this fails.
        Assert.Equal(2, ExampleLines.Length);
        foreach (var line in ExampleLines)
        {
            Assert.Equal(line, CanonicalJson.Serialize(CanonicalJson.Deserialize(line)));
        }
    }

    [Fact]
    public void Hl7_example_reads_into_the_expected_typed_values()
    {
        var canonicalEvent = CanonicalJson.Deserialize(Hl7Example);

        Assert.Equal(SourceFormat.Hl7v2, canonicalEvent.Source.Format);
        Assert.Equal(new DateOnly(1944, 6, 8), canonicalEvent.Patient.BirthDate);
        Assert.Equal(Sex.Male, canonicalEvent.Patient.Sex);
        Assert.Equal(VisitClass.Inpatient, canonicalEvent.Visit!.Class);
        Assert.Null(canonicalEvent.Visit.End);
        // The BEL byte survives the JSON escaping as the real control character.
        Assert.Contains('\u0007', canonicalEvent.Unmapped.Single(u => u.Name == "ZPV").Raw);
    }

    [Theory]
    [InlineData("\"schema_version\":1,", "\"schema_version\":2,")]                           // future version
    [InlineData("\"content_hash\":\"9cdbcc0a8dcd8bb82c5015262218e63ce1588d036e2f7712402cd8c882b91f39\",", "")] // missing required field
    [InlineData("\"schema_version\":1,", "\"schema_version\":1,\"tenant_id\":\"t1\",")]      // unknown field
    [InlineData("\"source_id\":\"100067890\",", "\"source_id\":null,")]                       // null where not allowed
    [InlineData("\"class\":\"inpatient\"", "\"class\":\"icu\"")]                              // value outside the enum
    public void Lines_that_break_the_contract_are_rejected(string find, string replace)
    {
        var broken = Hl7Example.Replace(find, replace);
        Assert.NotEqual(Hl7Example, broken); // guard: the mutation really happened

        Assert.Throws<JsonException>(() => CanonicalJson.Deserialize(broken));
    }
}

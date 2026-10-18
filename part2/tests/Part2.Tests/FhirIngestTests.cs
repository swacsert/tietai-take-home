using Part2.Canonical;
using Part2.Ingest;
using Part2.Ingest.Fhir;

namespace Part2.Tests;

public class FhirIngestTests
{
    private static readonly IngestResult Bundle = Fixtures.IngestFhir();

    private static CanonicalEvent Encounter(string id) => Bundle.Events.Single(e => e.Source.RecordId == id);

    [Fact]
    public void One_event_per_encounter_and_nothing_left_unlinked_in_the_sample()
    {
        Assert.Equal(["enc-003-1", "enc-003-2", "enc-011-1", "enc-013-1", "enc-014-1"], Bundle.Events.Select(e => e.Source.RecordId));
        Assert.Empty(Bundle.DeadLetters);
        Assert.Empty(Bundle.UnlinkedResources);
        // 25 resources: 4 Patients + 5 Encounters, and the other 16 travel inside the events.
        var carried = Bundle.Events.Sum(e => e.Diagnoses.Count + e.Unmapped.Count);
        Assert.Equal(25 - 4 - 5, carried);
    }

    [Fact]
    public void Enc_011_1_produces_exactly_the_documented_example_line()
    {
        Assert.Equal(Fixtures.ExampleLines[1], CanonicalJson.Serialize(Encounter("enc-011-1")));
    }

    [Fact]
    public void Missing_gender_and_open_encounter_stay_null()
    {
        var morales = Encounter("enc-013-1");

        Assert.Null(morales.Patient.Sex);
        Assert.Null(morales.Patient.SexSource);
        Assert.Null(morales.Visit!.End);
    }

    [Fact]
    public void Resources_that_hang_from_no_encounter_are_counted()
    {
        const string bundle = """
            {"resourceType":"Bundle","id":"b1","type":"collection","timestamp":"2026-03-15T18:00:00+01:00","entry":[
              {"resource":{"resourceType":"Patient","id":"p1","identifier":[{"system":"urn:x","value":"1"}],"birthDate":"1970-01-01"}},
              {"resource":{"resourceType":"Patient","id":"p2","identifier":[{"system":"urn:x","value":"2"}],"birthDate":"1970-01-01"}},
              {"resource":{"resourceType":"Encounter","id":"e1","class":{"code":"AMB"},"subject":{"reference":"Patient/p1"},"period":{"start":"2026-03-15T09:00:00+01:00"}}},
              {"resource":{"resourceType":"Observation","id":"o1","subject":{"reference":"Patient/p1"}}},
              {"resource":{"resourceType":"Condition","id":"c1","encounter":{"reference":"Encounter/missing"}}}
            ]}
            """;

        var result = FhirBundleMapper.Map(bundle, "test.json");

        Assert.Single(result.Events);
        Assert.Equal(1, result.UnlinkedResources["Observation"]); // no encounter reference
        Assert.Equal(1, result.UnlinkedResources["Condition"]);   // references an encounter not in the bundle
        Assert.Equal(1, result.UnlinkedResources["Patient"]);     // p2: no encounter uses it
    }

    [Fact]
    public void Invalid_encounter_goes_to_the_dlq_as_a_replayable_bundle_and_the_rest_continue()
    {
        var broken = Fixtures.Read("fhir_bundle.json").Replace("\"start\": \"2026-03-14T10:00:00+01:00\"", "\"start\": \"not-a-date\"");

        var result = FhirBundleMapper.Map(broken, "fhir_bundle.json");

        var deadLetter = Assert.Single(result.DeadLetters);
        Assert.Contains("Encounter.period.start 'not-a-date'", deadLetter.Reason);
        Assert.Equal(4, result.Events.Count);
        // The dead letter is itself a bundle with the patient, the encounter and its linked resources.
        var replayed = FhirBundleMapper.Map(deadLetter.Raw.Replace("not-a-date", "2026-03-14T10:00:00+01:00"), "replay.json");
        Assert.Equal(Encounter("enc-013-1").ContentHash, Assert.Single(replayed.Events).ContentHash);
    }

    [Fact]
    public void A_file_that_is_not_json_goes_whole_to_the_dlq()
    {
        var result = FhirBundleMapper.Map("{ not json", "bad.json");

        Assert.Empty(result.Events);
        Assert.StartsWith("bundle rejected:", Assert.Single(result.DeadLetters).Reason);
    }
}

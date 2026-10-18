using Part2.Canonical;
using Part2.Ingest;
using Part2.Ingest.Hl7;

namespace Part2.Tests;

public class Hl7IngestTests
{
    private static readonly IngestResult Feed = Fixtures.IngestHl7();

    private static CanonicalEvent Message(string controlId) => Feed.Events.Single(e => e.Source.RecordId == controlId);

    [Fact]
    public void Adt00010_is_read_with_the_hash_and_at_separators_it_declares()
    {
        var adt = Message("ADT00010");

        Assert.Equal("#@~\\&", adt.Source.EncodingCharacters);
        // With '|' and '^' hard-coded, PID-3.1 would come out as "100090123@@@SAN_ANDRES@MR".
        Assert.Equal("100090123", adt.Patient.SourceId);
        Assert.Equal("SAN_ANDRES", adt.Patient.Issuer);
        Assert.Equal(new DateOnly(1961, 12, 1), adt.Patient.BirthDate);
        var diagnosis = Assert.Single(adt.Diagnoses);
        Assert.Equal("J44.1", diagnosis.Code);
        Assert.Equal("COPD with acute exacerbation", diagnosis.Display);
        // MSH-9 uses '^' although MSH-2 declares '@': kept as one component and flagged (D25).
        Assert.Equal("ADT^A01^ADT_A01", adt.Source.RecordType);
        Assert.Contains(adt.QualityWarnings, w => w.Code == "component_separator_mismatch" && w.Location == "MSH-9");
    }

    [Fact]
    public void Garbled_message_goes_to_the_dlq_with_a_reason_and_the_batch_goes_on()
    {
        var deadLetter = Assert.Single(Feed.DeadLetters);

        Assert.Equal(12, deadLetter.Position);
        Assert.StartsWith("GARBLED|TRANSMISSION", deadLetter.Raw);
        Assert.Contains("BROKEN_SEGMENT_NAME", deadLetter.Raw); // the whole unit, not just the first line
        Assert.Contains("first segment is not MSH", deadLetter.Reason);
        // 20 units in the file: 19 events plus the dead letter. Messages after it were still read.
        Assert.Equal(19, Feed.Events.Count);
        Assert.Contains(Feed.Events, e => e.Source.Position == 20);
    }

    [Fact]
    public void Every_z_segment_in_the_feed_lands_in_unmapped()
    {
        var zSegmentsInFile = Fixtures.Read("adt_feed.hl7")
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith('Z'))
            .ToList();
        var zSegmentsInEvents = Feed.Events
            .SelectMany(e => e.Unmapped)
            .Where(u => u.Name.StartsWith('Z'))
            .Select(u => u.Raw)
            .ToList();

        Assert.Equal(4, zSegmentsInFile.Count); // ZIN + ZPV in ADT00006 and in ADT00014
        Assert.Equal(zSegmentsInFile, zSegmentsInEvents);
    }

    [Fact]
    public void Both_copies_of_Adt00013_have_the_same_event_id_and_content_hash()
    {
        var copies = Feed.Events.Where(e => e.Source.RecordId == "ADT00013").ToList();

        Assert.Equal(2, copies.Count);
        Assert.NotEqual(copies[0].Source.SentAt, copies[1].Source.SentAt); // they really differ in MSH-7
        Assert.Equal(copies[0].EventId, copies[1].EventId);
        Assert.Equal(copies[0].ContentHash, copies[1].ContentHash);
    }

    [Fact]
    public void Changing_a_clinical_value_changes_the_content_hash()
    {
        var original = Feed.Events.First(e => e.Source.RecordId == "ADT00013");
        var text = Fixtures.Read("adt_feed.hl7").Replace("K35.80^Unspecified", "K35.89^Unspecified");

        var changed = Hl7Feed.Ingest(text, "adt_feed.hl7").Events.First(e => e.Source.RecordId == "ADT00013");

        Assert.Equal(original.EventId, changed.EventId);
        Assert.NotEqual(original.ContentHash, changed.ContentHash);
    }

    [Fact]
    public void Cr_lf_and_crlf_line_endings_give_identical_events()
    {
        var withLf = Fixtures.Read("adt_feed.hl7").Replace("\r\n", "\n");

        string Serialized(string text) =>
            string.Join("\n", Hl7Feed.Ingest(text, "adt_feed.hl7").Events.Select(CanonicalJson.Serialize));

        var expected = Serialized(withLf);
        Assert.Equal(expected, Serialized(withLf.Replace('\n', '\r')));
        Assert.Equal(expected, Serialized(withLf.Replace("\n", "\r\n")));
    }

    [Fact]
    public void Adt00014_produces_exactly_the_documented_example_line()
    {
        Assert.Equal(Fixtures.ExampleLines[0], CanonicalJson.Serialize(Message("ADT00014")));
    }

    [Fact]
    public void Missing_birth_date_is_null_not_invented()
    {
        Assert.Null(Message("ADT00009").Patient.BirthDate);
    }

    [Fact]
    public void A03_without_pv1_45_takes_the_discharge_time_from_evn_2()
    {
        var discharge = Message("ADT00011");

        Assert.Equal(new DateTime(2026, 3, 15, 13, 30, 0), discharge.Visit!.End);
        Assert.Contains(discharge.QualityWarnings, w => w.Code == "discharge_datetime_from_evn");
    }

    [Fact]
    public void Oru_results_have_no_visit_and_keep_obr_and_obx_in_unmapped()
    {
        var oru = Message("ORU00001");

        Assert.Null(oru.Visit);
        Assert.Equal(["OBR", "OBX"], oru.Unmapped.Select(u => u.Name));
    }

    [Fact]
    public void Dead_letter_is_written_raw_with_its_reason_file()
    {
        var outDirectory = Path.Combine(Path.GetTempPath(), "part2-tests-" + Guid.NewGuid());
        try
        {
            OutputWriter.Write(Feed, outDirectory, "adt_feed.hl7");

            var dlq = Path.Combine(outDirectory, "dlq");
            Assert.StartsWith("GARBLED|", File.ReadAllText(Path.Combine(dlq, "adt_feed.0012.hl7")));
            Assert.Contains("first segment is not MSH", File.ReadAllText(Path.Combine(dlq, "adt_feed.0012.reason.txt")));
            var jsonl = File.ReadAllText(Path.Combine(outDirectory, "canonical", "adt_feed.jsonl"));
            Assert.Equal(19, jsonl.Count(c => c == '\n'));
            Assert.DoesNotContain('\r', jsonl);
        }
        finally
        {
            Directory.Delete(outDirectory, recursive: true);
        }
    }
}

# Take-Home Fixtures — Hospital "San Andres"

These files are the input data for **Part 2** of the take-home exercise.
Part 3 (the DQ agent) runs on top of whatever your Part 2 pipeline produces.

```
part2/data/
  adt_feed.hl7         HL7 v2.5 ADT and ORU messages from the legacy HIS
  fhir_bundle.json     FHIR R4 Bundle from the oncology unit
  vocab_icd10.csv      ICD-10 -> OMOP standard_concept_id mapping (small stub)
```

## adt_feed.hl7

Roughly 20 HL7 v2.5 messages, mostly `ADT^A01` / `A04` / `A08` / `A03` with a
couple of `ORU^R01` results mixed in. Messages are separated by a blank line;
segments within a message are separated by `\n`. Real-world feeds use `\r` —
your parser should tolerate either.

The feed is intentionally messy. You will find at least:
- Z-segments interleaved with standard segments.
- A message whose **field and component separators are non-standard** (declared
  in `MSH-1` / `MSH-2`). Do not hard-code `|` and `^`.
- A message **missing PID-7** (birth date).
- A message with a **non-printable byte** inside a field value.
- A message that is **fully malformed**. It must land in a dead-letter
  directory with the reason logged — not crash the run.
- Two messages with the **same `MSH-10` control ID** within the same day.

If you spot more, tell us about it in your README.

## fhir_bundle.json

A FHIR R4 `Bundle` (collection) from the oncology unit at San Andres for
2026-03-15. Patients overlap with `adt_feed.hl7` (`P003`, `P011`); the rest are
oncology-only. Resources included: `Patient`, `Encounter`, `Condition`,
`Observation`, `MedicationStatement`.

The bundle has its own real-world ugliness. Find what you find. Decide what to
quarantine vs. let through. Tell us why in your README.

## vocab_icd10.csv

A small stub mapping `ICD-10-CM` source codes to invented OMOP
`standard_concept_id` values. The structure mirrors the real OMOP
`concept_relationship` "Maps to" edge.

```
source_code,source_vocabulary_id,source_name,
target_concept_id,target_vocabulary_id,target_concept_name,domain_id
```

Not every code in the data is in this CSV. We want to see what your loader
does when a source code has no `standard_concept_id`.

## Scope reminder

Hit the headline goals first:

1. Read the HL7 feed and the FHIR bundle.
2. Normalize each to a canonical event format on disk (JSONL).
3. Load the canonical events into OMOP tables (Person, Visit_occurrence,
   Condition_occurrence) in SQLite or DuckDB.
4. Survive the obvious ugliness. Dead-letter what you cannot save.



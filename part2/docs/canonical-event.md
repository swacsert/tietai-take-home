# Canonical event — contract v1

The canonical JSONL is the **only** thing `Part2.Ingest` and `Part2.OmopWriter` share.
It simulates a Hydra channel boundary: the ingestor writes it, the writer reads it, and
either side can be rerun or replaced as long as this contract holds.

- Normative example: [`canonical-event.example.jsonl`](canonical-event.example.jsonl).
  The test suite parses it and re-serializes it byte for byte, so this document's example
  and the C# types in `Part2.Canonical` cannot drift apart silently.
- C# types: `src/Part2.Canonical`. A consumer in another language only needs this document.

## Files

- UTF-8, one JSON object per line, lines end with `\n`.
- One JSONL file per input file (for example `adt_feed.hl7` → `adt_feed.jsonl`).
- The writer never deletes or edits a JSONL file. Rerunning the writer replays it.

## One line = one source unit

| Source | One event is |
|---|---|
| HL7 v2 | One message (from `MSH` to the blank line that ends it) |
| FHIR R4 | One `Encounter` plus its `Patient` and every resource that references it (`encounter` or `context`) |

These never become events, and they are never dropped silently:

- An HL7 message the ingestor cannot parse goes to `out/dlq/` with the reason.
- A FHIR Encounter that cannot be mapped goes to `out/dlq/` as the Encounter's JSON, exactly as it is
  in the bundle. Its Patient and linked resources stay in the input file. A bundle that is not valid
  JSON goes whole, as position 0.
- A FHIR resource that hangs from no `Encounter` is **counted** in the ingestor's run summary, by resource type.

## Identity and deduplication

| Field | HL7 | FHIR |
|---|---|---|
| `event_id` | `hl7:{MSH-3}:{MSH-10}` | `fhir:{Bundle.id}:Encounter/{id}` |
| `content_hash` | SHA-256 of the message with `MSH-7` and `EVN-2` emptied | SHA-256 of the event's FHIR resources |

`event_id` says *which* message this is. `content_hash` says *what* it contained.
The writer applies this rule, because only the writer keeps state across runs:

| Seen before? | Meaning | Writer action |
|---|---|---|
| Same `event_id`, same `content_hash` | Retransmission | Ignore |
| Same `event_id`, different `content_hash` | Conflict | Flag it and keep the data already loaded |
| New `event_id` | New event | Load |

So `event_id` is **not** unique inside a JSONL file. The sample feed sends `ADT00013` twice:
same patient, visit and diagnosis, only `MSH-7` and `EVN-2` differ. Both lines are written,
with the same `event_id` and the same `content_hash`. An `A08` update carries its own `MSH-10`,
so it is a new event, not a conflict.

How the hash is computed:

- **HL7**: split segments on `\r`, `\n` or `\r\n` and drop empty lines. Empty the values of `MSH-7`
  and `EVN-2` but keep their fields. Join the segments with `\r` and hash the UTF-8 bytes.
  `MSH-7` and `EVN-2` are transmission timestamps, and a resend changes them.
- **FHIR**: take the `Patient`, then the `Encounter`, then the linked resources in bundle order.
  Serialize each one as minified JSON, join them with `\n` and hash the UTF-8 bytes.
- Lowercase hex, 64 characters.
- Consumers treat the hash as **opaque**: compare it for equality, never recompute it.

## Fields

`?` = nullable. Every field is always present, with `null` when it has no value.

| Field | Type | HL7 source | FHIR source | Notes |
|---|---|---|---|---|
| `schema_version` | int | — | — | `1` |
| `event_id` | string | see above | see above | |
| `content_hash` | string | see above | see above | |
| `source.format` | `hl7v2` \| `fhir_r4` | | | |
| `source.file` | string | input file name | input file name | |
| `source.position` | int | message number in the file, from 1 | entry number of the `Encounter`, from 1 | Counts every message, including the ones sent to the DLQ |
| `source.record_id` | string | `MSH-10` | `Encounter.id` | |
| `source.record_type` | string | `MSH-9`, split with the declared component separator and re-joined with `^` (`ADT^A01^ADT_A01`) | `Encounter` | The ingestor uses the trigger event `A03` (discharge) |
| `source.sent_at` | datetime | `MSH-7` | `Bundle.timestamp` | |
| `source.sender` | string? | `MSH-3` | `null` (the bundle has no sender) | |
| `source.encoding_characters` | string? | `MSH-1` + `MSH-2` (`\|^~\&`) | `null` | Needed to re-read the raw segments in `unmapped` |
| `patient.source_id` | string | `PID-3.1` | `Patient.identifier.value` | |
| `patient.issuer` | string | `PID-3.4` | `Patient.identifier.system` | **As received.** Matching `SAN_ANDRES` with the FHIR OID is the writer's job |
| `patient.birth_date` | date? | `PID-7` | `Patient.birthDate` | `null` if absent. Never invented |
| `patient.sex` | `female` \| `male` \| `other` \| `unknown` ? | `PID-8` | `Patient.gender` | `null` if absent |
| `patient.sex_source` | string? | `PID-8` | `Patient.gender` | Original value, for `gender_source_value` |
| `visit` | object? | `PV1` | `Encounter` | `null` when there is no visit (`ORU` messages) |
| `visit.source_id` | string | derived: `{PID-3.1}:{admit datetime as received}` | `Encounter.id` | See below |
| `visit.class` | `inpatient` \| `outpatient` \| `emergency` | `PV1-2` (`I`, `O`, `E`); `emergency` also when `PV1-10` (hospital service) = `ER` | `Encounter.class.code` (`IMP`, `AMB`, `EMER`) | This HIS sends emergency visits as `PV1-2 = O` + `PV1-10 = ER`; `class_source` keeps `O` |
| `visit.class_source` | string | `PV1-2` | `Encounter.class.code` | |
| `visit.start` | datetime | admit datetime | `Encounter.period.start` | |
| `visit.end` | datetime? | `PV1-45`; for an `A03` without it, `EVN-2` (with a warning) | `Encounter.period.end` | `null` if absent. The writer decides the OMOP fallback |
| `diagnoses[].code` | string | `DG1-3.1` | `Condition.code.coding.code` | |
| `diagnoses[].system` | string | `DG1-3.3` | `Condition.code.coding.system` | Normalized: `ICD10CM` |
| `diagnoses[].system_source` | string | `DG1-3.3` (`I10`) | coding system URI | |
| `diagnoses[].display` | string? | `DG1-3.2` | `Condition.code.coding.display` | |
| `diagnoses[].start_date` | date? | `DG1-5` | `Condition.onsetDateTime` | |
| `unmapped[]` | list | | | See below |
| `quality_warnings[]` | list | | | See below |

### Normalization rules

- **Datetimes** are the facility's local wall time, without offset: `2026-03-15T14:00:00`.
  HL7 timestamps in this feed have no offset. From FHIR the ingestor keeps the local time its own
  offset describes. OMOP stores datetimes without a time zone too. With several tenants, the time
  zone would come from each tenant's configuration (see the README).
- **Dates** are `yyyy-MM-dd`.
- **`visit.source_id` for HL7 is derived** because `PV1-19` (visit number) is unusable in this feed:
  it contains `VIP`. The admit datetime is kept **in full, as received** (`20260315150500`), never
  truncated to the date, so two visits on the same day stay apart. The `A01`, `A08` and `A03` of
  one stay share it.
- **Admit datetime**: `PV1-44` when it has a value. Otherwise `PV1-48`, with the warning
  `admit_datetime_nonstandard_field`. This feed sends it in `PV1-48`.
- **Separators** are always the ones the message declares in `MSH-1` and `MSH-2`, never guessed.
  `ADT00010` declares `@` as component separator but writes `MSH-9` with `^`. There `MSH-9` stays one
  component (`ADT^A01^ADT_A01`) and the event carries `component_separator_mismatch`.
- **`ICD10CM`**: HL7 `I10` formally means ICD-10, but the codes in the feed (for example `K35.80`)
  are ICD-10-CM, the vocabulary in `vocab_icd10.csv`.

### What the ingestor does *not* do

The ingestor is faithful to the source. These are writer decisions, so they are not in the event:
vocabulary mapping, the fallback for a missing `visit.end`, matching patients across issuers,
and quarantining a patient without a birth date.

### `unmapped[]`: nothing is dropped silently

`{ "kind": "hl7_segment" | "fhir_resource", "name": string, "raw": string }`

- **HL7**: every segment the ingestor does not read (`ZIN`, `ZPV`, `NK1`, `OBR`, `OBX`, …) is kept
  whole, with the message's own separators. `MSH`, `EVN`, `PID`, `PV1` and `DG1` are read, so they
  are not repeated here. Fields of those segments that the event does not need (names, address,
  phone) are not carried.
- **FHIR**: every linked resource the ingestor does not map (`Observation`, `MedicationStatement`)
  is kept as minified JSON.

### `quality_warnings[]`

`{ "code": string, "location": string, "detail": string }`

Only for problems you cannot see from the typed fields. A `null` birth date needs no warning.

| Code | Example |
|---|---|
| `control_character` | Byte `0x07` inside `ZPV-3` of `ADT00014`. The value is kept as received |
| `admit_datetime_nonstandard_field` | Admit datetime read from `PV1-48` because `PV1-44` is empty |
| `component_separator_mismatch` | `MSH-9` of `ADT00010` contains `^` although `MSH-2` declares `@` |
| `discharge_datetime_from_evn` | `A03` without `PV1-45`: discharge datetime taken from `EVN-2` |

## Personal data

The typed fields carry no names, addresses or phone numbers, because OMOP does not need them.
The raw values in `unmapped` can still contain personal data (for example `NK1`: next of kin, with
a name and a phone number). **Treat the JSONL as personal health data**, with the same access
controls and retention rules as the raw feed.

## Versioning and strictness

The reader (`CanonicalJson.Deserialize`) rejects a line that has:

- another `schema_version`,
- an unknown field,
- a missing required field,
- `null` in a field that is not nullable,
- a value outside an enum.

A broken contract fails loudly at the boundary. It does not turn into a silent `null` in OMOP.

**Planned for v2: `tenant_id`.** A required top-level string, set by the ingestor from its
command line. It also prefixes `event_id` (`{tenant}:hl7:…`) and becomes part of every unique key
in OMOP. A v1 reader rejects v2 lines (unknown field, other version) instead of loading them
without a tenant.

## Example

The HL7 line of the normative example, pretty-printed:

```json
{
  "schema_version": 1,
  "event_id": "hl7:HIS_SANANDRES:ADT00014",
  "content_hash": "91e07f34450693de4c9bbd1c1b4d48ce89fc72fddcb5faafeed34556f194f35f",
  "source": {
    "format": "hl7v2",
    "file": "adt_feed.hl7",
    "position": 17,
    "record_id": "ADT00014",
    "record_type": "ADT^A01^ADT_A01",
    "sent_at": "2026-03-15T15:05:00",
    "sender": "HIS_SANANDRES",
    "encoding_characters": "|^~\\&"
  },
  "patient": {
    "source_id": "100067890",
    "issuer": "SAN_ANDRES",
    "birth_date": "1944-06-08",
    "sex": "male",
    "sex_source": "M"
  },
  "visit": {
    "source_id": "100067890:20260315150500",
    "class": "inpatient",
    "class_source": "I",
    "start": "2026-03-15T15:05:00",
    "end": null
  },
  "diagnoses": [
    { "code": "F03", "system": "ICD10CM", "system_source": "I10",
      "display": "Unspecified dementia", "start_date": "2026-03-15" }
  ],
  "unmapped": [
    { "kind": "hl7_segment", "name": "ZIN", "raw": "ZIN|1|MUTUA_MADRILENA|Z-998877|PRIVATE" },
    { "kind": "hl7_segment", "name": "ZPV",
      "raw": "ZPV|1|EMERGENCY_DEPT|Readmission within 30 days\u0007 - cognitive decline progression" }
  ],
  "quality_warnings": [
    { "code": "admit_datetime_nonstandard_field", "location": "PV1-48",
      "detail": "admit datetime in non-standard field (PV1-44 is empty)" },
    { "code": "control_character", "location": "ZPV-3", "detail": "byte 0x07 kept as received" }
  ]
}
```

The second line of the example file is the FHIR event for `enc-011-1`. It has two diagnoses
(`C18.99` has no mapping; the writer loads it with `concept_id = 0`) and three unmapped resources,
one of them the implausible HbA1c of 25 %.

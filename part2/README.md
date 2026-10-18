# Part 2 — HL7 v2 + FHIR R4 → canonical JSONL → OMOP

Two batch components that talk only through files on disk:

```
data/adt_feed.hl7 ────┐
                      ├─> Part2.Ingest ─> out/canonical/*.jsonl ─> Part2.OmopWriter ─> out/omop.db
data/fhir_bundle.json ┘         │
                                └─> out/dlq/  (units that cannot be read, with the reason)
```

`out/omop.db` holds `person`, `visit_occurrence`, `condition_occurrence` and the loader's `etl_*` tables.

The canonical JSONL is the contract between them: [docs/canonical-event.md](docs/canonical-event.md).

## Run it

Needs the .NET 10 SDK. Run from this folder (`part2/`).

| What | Command |
|---|---|
| Ingest component | `dotnet run --project src/Part2.Ingest -- --out out data/adt_feed.hl7 data/fhir_bundle.json` |
| OMOP writer component | `dotnet run --project src/Part2.OmopWriter -- --db out/omop.db out/canonical` |
| End to end (clean run, then the writer a second time to show idempotency) | `./run.sh` or `./run.ps1` |
| Tests (39, xUnit, on the real sample files) | `dotnet test` |

The writer's other options have defaults: `--vocab data/vocab_icd10.csv` and `--issuers config/issuers.json`.
Each component can be rerun on its own. The ingestor rewrites its outputs, and its output depends
only on its input (no timestamps). The writer never deletes the JSONL, so running it again replays it.

## Why C#

C# is the language I write production integration code in (HL7 v2 feeds, Mirth, SQL Server), so I can
defend every line. It also gives the contract static types, and each component runs with one
`dotnet run`. The only package beyond the SDK is `Microsoft.Data.Sqlite`. I wrote the small HL7 parser
myself instead of using NHapi, because reading separators from `MSH` is the point of the exercise.

## Why the boundaries are where they are

- **The ingestor knows the sources and nothing about OMOP. The writer knows OMOP and nothing about
  HL7 or FHIR syntax.** Between them is one versioned, documented, strictly validated JSONL format. You
  can swap either side (a Mirth/Hydra channel in front, a Postgres or Databricks writer behind) as long
  as the contract holds. A consumer in another language only needs the document.
- **Unreadable vs. unloadable.** If a unit cannot be read (no `MSH`, broken separators, bad timestamp),
  it is an ingest problem: it goes to `out/dlq/`. If an event is readable but cannot go into OMOP
  (no birth date, a conflicting resend), it is a writer problem: it goes to the `etl_quarantine` table. That is why the patient without `PID-7` is
  quarantined and not dead-lettered: the message was fine, OMOP just cannot hold it.
- **The ingestor is faithful to the source.** It never defaults, maps vocabulary or merges patients:
  a missing visit end stays `null`. All OMOP rules live in the writer (end date = start date, concept 0,
  issuer equivalence, domain routing). Changing an OMOP rule means rerunning only the writer from the
  JSONL already on disk, with no re-ingest.
- **The shared library `Part2.Canonical` holds only the contract**: record types plus JSON options.
  A test parses the documented example and re-serializes it byte for byte, so the document and the
  code cannot drift apart without a failing test.

## OMOP model

| Table | One row per | Natural key (`UNIQUE`) |
|---|---|---|
| `person` | real patient | `person_source_value` = `{canonical issuer}:{patient id}` |
| `visit_occurrence` | source visit (N per person) | `(person_id, visit_source_value)`: `hl7:{patient}:{admit datetime}` or `fhir:{encounter id}` |
| `condition_occurrence` | diagnosis code on a visit (N per visit) | `(visit_occurrence_id, condition_source_value)` |

- The tables have all the OMOP CDM 5.4 columns. Unused columns are `NULL`, and foreign keys are enforced.
- Concepts: gender 8507 / 8532 (otherwise 0), visit 9201 / 9202 / 9203, type concept 32817 (EHR).
  Race and ethnicity are 0.
- **Emergency visits**: this HIS sends them as `PV1-2 = O` with hospital service `PV1-10 = ER`. The
  ingestor maps that to `emergency`, so they load as 9203 (Emergency Room Visit), not 9202. `class_source`
  still keeps `O`.
- **Person / visit join.** Each visit references the one person resolved from its event.
  `config/issuers.json` says that HL7's `SAN_ANDRES` and FHIR's `urn:oid:2.16.724.4.40.100.1` are the same
  hospital. Patients `100034567` and `100123456` therefore end up as one row each, with visits from both
  sources. This is configuration, not code: a new tenant brings its own file.
- **Vocabulary mapping is pluggable.** The loader only sees `IConceptMapper.Map(vocabulary, code)`.
  Today `CsvConceptMapper` reads `vocab_icd10.csv`, which mirrors OMOP's "Maps to" relationship. In
  production the same interface would query `concept_relationship`.
  - **No mapping** (`C18.99`, `C25.9`): the row loads with `condition_concept_id = 0` and the code in
    `condition_source_value` (OMOP convention).
  - **Domain routing**: OMOP puts each record in the table of its concept's domain.
    - `Symptom` (`R07.9` in the stub) is not an OMOP domain. In OMOP, symptoms are Condition-domain
      concepts, so these rows load as conditions.
    - `Observation` (`O80`, `Z51.11`): that table is out of scope. The diagnosis is not loaded and the
      run report counts it as `domain Observation: table out of scope`. It is not quarantined: nothing
      is wrong with it.
- **Open visits**: `visit_end_date` = start date and `visit_end_datetime` stays `NULL`. Using the
  extraction date would give a different result every day and break idempotency. A later `A03` with a
  discharge time closes the visit.
- **Idempotency and dedup** (table `etl_event`):
  - same `event_id` + same `content_hash` → retransmission or rerun, skipped;
  - same `event_id` + different hash → conflict, quarantined, and the loaded data is kept;
  - new `event_id` → loaded.

  The hash leaves out `MSH-7` and `EVN-2`, so the two copies of `ADT00013` are recognised as one message.
- **Corrections (`A08`): diagnoses accumulate, they are never deleted.** A code already on the visit
  stays as it is; a new code is added. Events are processed by `sent_at`, so the first message that
  brought a code fixes its start date, whatever the file order. The order in which replays arrive does
  not matter. How a sender *withdraws* a diagnosis is an open question (below).

## Results on the sample data

| | Rows | Why |
|---|---|---|
| `person` | 12 | 11 HL7 patients − 1 quarantined + 2 FHIR-only patients (the 2 shared ones are not counted twice) |
| `visit_occurrence` | 16 | 11 HL7 stays (A01/A08/A03 of one stay share the key; the `ADT00013` resend is skipped) + 5 FHIR encounters |
| `condition_occurrence` | 18 | 13 from HL7 + 5 from FHIR. Diagnoses accumulate per visit; `O80` is not loaded |
| `out/dlq/` | 1 unit | `adt_feed.hl7` position 12: first segment is not `MSH` |
| `etl_quarantine` | 1 row | `ADT00009`: no birth date |
| Out of scope (counted in the report) | 1 diagnosis | `O80` (`ADT00008`): domain Observation |

Running the writer a second time loads 0 events and leaves exactly these counts (asserted by a test and shown by `run.sh`).

## Findings in the data, and what the pipeline does with them

| Finding | Where | Decision |
|---|---|---|
| Field `#` / component `@` separators | `ADT00010` | Read from `MSH-1`/`MSH-2`; nothing is hard-coded |
| **The same message contradicts itself**: it declares `@`, but `MSH-9` is written with `^` | `ADT00010` | Declared separators are honoured everywhere, never guessed. `MSH-9` stays one component and the event carries `component_separator_mismatch`. Accepted: the rest of the message uses `@` consistently, and the message type does not change what gets loaded |
| Garbage unit without `MSH` | position 12 | DLQ with reason; the batch goes on |
| `MSH-10` `ADT00013` sent twice | 14:25 and 17:15 | Same patient, visit and diagnosis: a retransmission. Same `event_id` and hash, loaded once |
| Missing `PID-7` | `ADT00009` | Quarantined. A birth year is never invented |
| Byte `0x07` in free text | `ZPV-3`, `ADT00014` | Accepted, value kept raw, `control_character` warning. It is not clinical data, so dead-lettering would lose a valid admission |
| Z-segments `ZIN`, `ZPV` | `ADT00006`, `ADT00014` | Kept whole in `unmapped` (a test checks all 4 are there) |
| **Admit datetime in `PV1-48`, not `PV1-44`** | all 16 `PV1` | `PV1-44` if filled, otherwise `PV1-48` with `admit_datetime_nonstandard_field` |
| **`PV1-19` (visit number) contains `VIP`**: the `PV1` fields are shifted | all 16 `PV1` | No usable visit number; the visit key is patient + full admit datetime |
| Emergency visits sent as `PV1-2 = O` with `PV1-10 = ER` | 6 messages | Loaded as 9203 (Emergency Room Visit) |
| `A03` without `PV1-45` | `ADT00011` | Discharge taken from `EVN-2`, with `discharge_datetime_from_evn` |
| `A08` changes a diagnosis (`R07.9` → `I21.4`) | `ADT00015` | `I21.4` is added, `R07.9` stays: diagnoses accumulate |
| `A03` re-sends only part of the diagnoses (`I50.9`, not the `E11.9` the `A08` added) | `ADT00011` | Nothing is deleted: the visit keeps both |
| 3 `ORU^R01` inside the ADT feed | `ORU00001-3` | Read; no visit; `OBR`/`OBX` kept in `unmapped`. Measurement is out of scope |
| Two issuers for one hospital | `SAN_ANDRES` vs `urn:oid:2.16.724.4.40.100.1` | Equivalence in `config/issuers.json` |
| **Probably the same visit in both sources**, no shared id | `100034567`: HL7 09:01 `O` vs FHIR 09:00 `AMB`; `100123456`: HL7 14:00 `I` vs FHIR 14:00 `AMB` | **Not merged**: two `visit_occurrence` rows for the same person, and `C18.2` appears on both. A false merge is worse than a duplicate. Flagged here as a probable duplicate |
| Open encounters | 4 of 5 in FHIR | End date = start date, end datetime `NULL` |
| Patient without `gender` | FHIR `p-013` | `gender_concept_id = 0`, not guessed |
| Unmapped codes | `C18.99` (not a real ICD-10-CM code), `C25.9` (not in the stub) | Concept 0, source code kept |
| **Vocabulary domains**: `O80`, `Z51.11` → `Observation`; `R07.9` → `Symptom` (not an OMOP domain) | `vocab_icd10.csv` | `Symptom` loads as Condition; `Observation` is counted as out of scope |
| Implausible values: HbA1c 25 %, weight 0.5 kg, PSA dated 2099 | FHIR `Observation` | Found, out of scope (no Measurement table). Kept raw in `unmapped`, not loaded |
| RxNorm `40048` named both Capecitabine and Gemcitabine | FHIR `MedicationStatement` | Found, out of scope (no `drug_exposure`) |
| Same patient with different names (`TORRES ELENA ISABEL` / `TORRES ELENA`) | HL7 vs FHIR | No effect: matching is by identifier, and names are not carried |

## openEHR (sketch, not implemented)

openEHR slots in as one more component on either side of the same boundary. Nothing that exists changes.

- **As a source**: an `OpenEhr` mapper next to `Hl7` and `Fhir` in the ingestor. It reads compositions
  (AQL over the CDR's REST API, e.g. EHRbase). An encounter composition (`openEHR-EHR-COMPOSITION.encounter.v1`)
  becomes the visit, its `EVALUATION.problem_diagnosis` entries become the diagnoses (`DV_CODED_TEXT`
  with an ICD-10 terminology id), and the EHR's subject `external_ref` (id + namespace) becomes
  `patient.source_id` + `issuer`. `content_hash` = hash of the composition, `event_id` = composition uid
  (`::version` excluded, so new versions show up as conflicts or updates). Entries without a mapping
  go to `unmapped` as canonical JSON, as with FHIR.
- **As a sink**: a third component that reads the same JSONL and posts compositions built from a
  template (FLAT format) to a CDR. It is useful when the customer wants a longitudinal clinical record
  and not only an analytics model.
- The canonical event would need no new fields for this. Archetype paths would live in the mapper,
  not in the contract.

## Production, multi-tenant, in Hydra

**Tenant stamping.**
- The tenant comes from the channel configuration, never from message content (an upstream system
  can put anything in `MSH-4`).
- The ingestor takes `--tenant`. Contract v2 adds a required `tenant_id` and prefixes `event_id` with it.
  The strict reader makes a v1 writer reject v2 lines instead of loading them without a tenant.
- I would give each tenant its own OMOP database or schema rather than a `tenant_id` column. That gives
  isolation for health data, per-tenant retention and deletion, and no cross-tenant query by mistake.
  The writer refuses events whose tenant does not match its target.
- Per-tenant configuration: issuer aliases, vocabulary version, and **time zone**. Today datetimes are
  facility wall time without offset; with tenants in different zones the zone must come from tenant config.

**Observability.**
- Today: a console summary per run and exit codes (0 = ran, 2 = bad arguments or input missing).
  A DLQ entry does not fail the run.
- In production:
  - structured logs per unit (`tenant`, `event_id`, `position`, outcome, reason code), and never
    message content;
  - counters for events in/out, DLQ, quarantine by reason, conflicts and out-of-scope domains;
  - `event_id` as the correlation id across both components;
  - alerts on DLQ or quarantine rate per tenant and on feed silence.

**DLQ replay with dedup.** Most of it is already in place:
- Each DLQ entry is the raw unit plus a `.reason.txt`.
  - An HL7 entry is the whole message: once fixed, it can go back through the ingestor as is.
  - A FHIR entry is the rejected Encounter's JSON as it was in the bundle; its Patient and linked
    resources are still in the input file. To replay it, fix the bundle and ingest it again: the
    Encounters already loaded are skipped by dedup.
- The writer's `etl_event` dedups the result:
  - an unchanged replay is skipped;
  - a changed one with the same id is flagged as a conflict instead of overwriting;
  - diagnoses only accumulate, so the order in which replays arrive does not matter.
- Quarantined events keep their canonical line in `etl_quarantine.line`. When the cause is fixed
  (a birth date arrives), they can be resent the same way.
- Missing for production:
  - a `replay` command that moves entries to `dlq/replayed/` with an audit record;
  - an explicit policy for conflicts (who decides which version wins).

**Compliance posture.**
- Everything here is special-category health data (GDPR art. 9, and LOPDGDD in Spain): the raw feed,
  the JSONL (its `unmapped` raw segments can carry names and phones, for example `NK1`), the DLQ and
  `person_source_value` (an MRN, the hospital's medical record number).
- Production needs: encryption at rest and in transit, least-privilege access per tenant, retention
  limits on JSONL/DLQ, audit logging, EU data residency, and pseudonymised `person_source_value`
  (keyed HMAC per tenant) in the analytics copy.
- **No message content in logs.** DLQ reasons and console output carry only the reason, the file and
  the position. They name the field (`PID-7 is not a supported HL7 date`), never its value. A test
  checks this. The raw unit itself stays in the DLQ file, which is protected like the feed.

## Left out, honestly

- Only the three requested tables. No Measurement, Observation or Drug_exposure, so ORU results,
  observations and medications are carried in `unmapped` but not loaded.
- HL7:
  - escape sequences (`\F\`, `\S\`, …) are not decoded;
  - timestamps with fractions or offsets are rejected to the DLQ;
  - `MSH-18` (character set) is ignored and files are read as UTF-8;
  - only the first repetition of a field is used.
- FHIR:
  - only `Type/id` references (no `urn:uuid` / `fullUrl` resolution);
  - only the first identifier and the first coding are used;
  - partial dates (`1980`) are rejected.
- Person demographics: the first event wins. There is no merge or conflict detection between sources
  for birth date or sex (they agree in this sample).
- No cross-source visit matching (deliberate, see findings). One-to-many "Maps to" relationships are
  rejected at load. `condition_source_concept_id` is 0 because the stub has no source concept ids.
- One writer at a time on one SQLite file. There is no concurrency control beyond the single transaction per run.
- Part 3 (the DQ agent) is not part of the brief I received.

## What I would ask the customer

- **How does this sender withdraw a diagnosis?** Today diagnoses only accumulate: `ADT00011` (`A03`)
  re-sends only `I50.9`, and the `E11.9` added by the `A08` stays. Is a diagnosis that disappears from
  a later message withdrawn, or just not re-sent? Is there an explicit signal (a `DG1` action code,
  an `A08` with a status)?
- Should the Observation table come into scope? `O80` (delivery) is the only diagnosis in the sample
  that is not loaded today.
- Is `PV1-48` for the admit datetime a known profile of this HIS?

## AI use

Built with Claude Code under my direction, block by block. Every proposal I rejected or corrected is
logged, with the date and the reason, in [AI_LOG.md](AI_LOG.md) (in Spanish). Three AI errors were
caught by tests or by checking against the data, not by reading.

What I double-checked myself is listed in the [root README](../README.md#ai-assistants-where-i-used-them-and-what-i-checked-myself).
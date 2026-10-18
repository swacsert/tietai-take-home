# Hospital San Andrés → OMOP: plan & plumb

Take-home exercise, two parts scored separately.

| Part | What | Where |
|---|---|---|
| 1 | Architecture sketch, tactical plan (phasing, top 5 risks, user empathy, decision log), pre-kickoff questions | [`part1/`](part1/) |
| 2 | HL7 v2.5 + FHIR R4 → canonical JSONL → OMOP CDM (SQLite), in C# / .NET 10 | [`part2/`](part2/) |

## How to run each part

**Part 1** is documents only:
- [`part1/architecture.md`](part1/architecture.md): Mermaid diagram (renders on GitHub) plus a PNG copy, with a caption for every non-obvious box.
- [`part1/plan.md`](part1/plan.md): the tactical plan.
- [`part1/questions.md`](part1/questions.md): the ten pre-kickoff questions.

**Part 2** needs the .NET 10 SDK. From `part2/`:

```bash
./run.sh            # or ./run.ps1 — build, ingest, load OMOP, load again to show idempotency
dotnet test         # 39 tests on the real sample files
```

The one-command-per-component invocations, design choices and data findings are in [`part2/README.md`](part2/README.md).

## Ambiguities I resolved

- **Part 3 (DQ agent).** The fixtures README mentions it, but the brief I received has two parts. I treated Part 3 as out of scope.
- **Language.** C# instead of the recommended Python/Go. It is the language I write production integration code in, so I can defend every line. The reasoning is in `part2/README.md`.

## AI assistants: where I used them and what I checked myself

I used AI the way I would on the job, and kept a log of what I rejected or corrected.

| Where | Assistant | What for |
|---|---|---|
| Part 1 | Claude (chat) | Reading the brief with me, proposing candidate lists of risks, push-backs and questions that I chose from, drafting the plan text, and generating the Mermaid diagram from my decisions |
| Part 2 | Claude Code (VS Code) | Writing the code and tests block by block from decisions recorded in [`CLAUDE.md`](CLAUDE.md), then a readability refactor to the classic C# style I write at work (output checked byte for byte against the previous version) |
| Part 2 | Claude (chat) | A second review of the generated code and of each data decision |

**Transparency files:**
- [`CLAUDE.md`](CLAUDE.md): the working context the coding assistant read: brief, decisions taken (with who decided each one), and data findings. It is in Spanish, my working language.
- [`part2/AI_LOG.md`](part2/AI_LOG.md): AI proposals I rejected or corrected, and why. For example: a diagnosis-replacement rule that silently deleted a real diagnosis, a wrong data finding about a duplicated `MSH-10`, and DLQ reasons that leaked patient data into logs.

**What I double-checked myself:**
- [ ] *(TODO Alonso: tick only what you actually checked.)* The separators are read from `MSH`: ran the parser on `ADT00010` (`#` and `@`).
- [ ] Rerunning the OMOP writer twice leaves the same row counts.
- [ ] Every data decision against the sample files (e.g. `ADT00013` is a resend, the admit time sits in `PV1-48`, the A03 has no `PV1-45`).
- [ ] Each test asserts something real.
- [ ] The standard concept ids used for gender and visit type (8507, 8532, 9201, 9202, 9203).

## Time spent

About **X h** in total (Part 1 ≈ **Y h**, Part 2 ≈ **Z h**), over the 3–5 h guideline. *(TODO Alonso: real numbers.)* Most of the extra time went into reviewing AI-generated work and checking every data decision against the sample files, so that I can defend it. In Part 2, C# also meant writing small pieces by hand (HL7 parser, CSV reader) instead of using libraries. With a strict 5-hour cap I would have stopped after the ingestion component and the OMOP schema, and listed the loader rules as next steps.

## What I would do next

1. **Tenant stamping:** add `tenant_id` to the canonical event (schema v2), stamp it at ingest and include it in every OMOP natural key.
2. **DLQ replay with dedup:** a command that re-ingests a fixed DLQ file. The writer's `event_id` + `content_hash` check already makes a replay safe.
3. **openEHR channel:** an AQL poller that maps the agreed templates to the same canonical event. The writer does not change.
4. **Pseudonymisation at the edge**, as in the Part 1 architecture: keyed hash of patient identifiers before the JSONL leaves the hospital, and no raw `unmapped` segments outside it.
5. **Observability:** counts per run (received, loaded, DLQ, quarantined) exported as metrics, with alerts on volume drops, the "HIS stopped writing" risk from Part 1.

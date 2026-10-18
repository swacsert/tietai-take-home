#!/usr/bin/env bash
# End to end: build, ingest both samples, load OMOP, then load again to show the rerun changes nothing.
set -euo pipefail
cd "$(dirname "$0")"

rm -rf out
dotnet build Part2.sln --nologo -v quiet

echo "=== 1. Ingest: HL7 + FHIR -> out/canonical/*.jsonl (bad units -> out/dlq/)"
dotnet run --project src/Part2.Ingest --no-build -- --out out data/adt_feed.hl7 data/fhir_bundle.json

echo "=== 2. OMOP writer: out/canonical/*.jsonl -> out/omop.db"
dotnet run --project src/Part2.OmopWriter --no-build -- --db out/omop.db out/canonical

echo "=== 3. OMOP writer again (idempotent: same row counts)"
dotnet run --project src/Part2.OmopWriter --no-build -- --db out/omop.db out/canonical

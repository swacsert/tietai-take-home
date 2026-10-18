# End to end: build, ingest both samples, load OMOP, then load again to show the rerun changes nothing.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Invoke-Step([string] $title, [scriptblock] $command) {
    if ($title) { Write-Host "=== $title" }
    & $command
    if ($LASTEXITCODE -ne 0) { throw "step failed with exit code $LASTEXITCODE" }
}

if (Test-Path out) { Remove-Item -Recurse -Force out }
Invoke-Step '' { dotnet build Part2.sln --nologo -v quiet }

Invoke-Step '1. Ingest: HL7 + FHIR -> out/canonical/*.jsonl (bad units -> out/dlq/)' {
    dotnet run --project src/Part2.Ingest --no-build -- --out out data/adt_feed.hl7 data/fhir_bundle.json
}
Invoke-Step '2. OMOP writer: out/canonical/*.jsonl -> out/omop.db' {
    dotnet run --project src/Part2.OmopWriter --no-build -- --db out/omop.db out/canonical
}
Invoke-Step '3. OMOP writer again (idempotent: same row counts)' {
    dotnet run --project src/Part2.OmopWriter --no-build -- --db out/omop.db out/canonical
}

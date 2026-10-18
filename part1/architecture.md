# 1a · Architecture sketch — San Andrés → OMOP CDM

> Rendered image (for viewers without Mermaid support): [`architecture.png`](architecture.png)

```mermaid
flowchart TB
  subgraph HOSP["San Andrés hospital network — restricted, no public ingress"]
    direction TB
    HIS["Legacy HIS<br/>HL7 v2.5 ADT + ORU"]:::src
    ONC["Oncology system<br/>FHIR R4 API"]:::src
    EHR["Chronic care EHR<br/>openEHR API (AQL)"]:::src
    DROP[("File drop folder")]:::store
    MARTA(["Marta — Hospital IT<br/>edge host (VM or server), outbound rule, change windows"]):::person
    HIS --> DROP
    subgraph EDGE["Hydra Connect — edge node inside the hospital"]
      direction TB
      CH1["Ch.1 HL7 file reader<br/>reads MSH separators, keeps Z-segments"]:::connect
      CH2["Ch.2 FHIR poller"]:::connect
      CH3["Ch.3 openEHR poller<br/>scope depends on agreed cohort"]:::connect
      CAN["Validate + normalise<br/>to canonical events"]:::connect
      EDLQ[("Local DLQ<br/>rejected + reason")]:::dlq
      PSE["Pseudonymisation<br/>keyed hash, key owned by hospital"]:::connect
      CH1 --> CAN
      CH2 --> CAN
      CH3 --> CAN
      CAN -- unparseable --> EDLQ
      CAN --> PSE
    end
    DROP --> CH1
    ONC --> CH2
    EHR --> CH3
    MARTA -.- EDGE
  end

  subgraph GKE["GKE europe-west1 — Hydra Platform, tenant san-andres"]
    direction TB
    LAND[("Landing store<br/>pseudonymised events, tenant-stamped,<br/>kept for replay")]:::store
    ID["Patient identity resolution<br/>one Person per real patient"]:::platform
    VOC["Vocabulary mapping<br/>CIE-10-ES → SNOMED, labs → LOINC<br/>pluggable, versioned"]:::platform
    DQ["Data quality checks<br/>implausible values → quarantine"]:::platform
    LOAD["OMOP loader<br/>idempotent"]:::platform
    REV["Review queue<br/>doubtful matches + unmapped codes"]:::platform
    OPS["Operations console<br/>monitoring, alerts, DLQ replay"]:::platform
    RUIZ(["Dr. Ruiz — clinical informaticist<br/>decides meaning"]):::person
    DIEGO(["Diego — junior data engineer<br/>runs channels after handover"]):::person
    WORK[("Working store<br/>reference data")]:::store
    LAND --> ID --> VOC --> DQ --> LOAD
    ID -. doubtful .-> REV
    VOC -. unmapped .-> REV
    RUIZ -.- REV
    DIEGO -.- OPS
    ID -.- WORK
    VOC -.- WORK
  end

  PSE == "outbound TLS only (443)" ==> LAND
  LOAD ==> OMOP[("OMOP CDM — owned by San Andrés<br/>Person · Visit_occurrence ·<br/>Condition_occurrence · Measurement")]:::store
  OMOP --> RES["Researchers<br/>first cohort, week 12"]:::out

  classDef src fill:#ECEFF1,stroke:#546E7A,color:#1F2933
  classDef connect fill:#E3F2FD,stroke:#1E63B5,color:#0D2B4D
  classDef platform fill:#E8F5E9,stroke:#2E7D32,color:#123A16
  classDef store fill:#FFF8E1,stroke:#B7791F,color:#4A3410
  classDef dlq fill:#FFEBEE,stroke:#C62828,color:#4A0E0E
  classDef person fill:#FCE4EC,stroke:#AD1457,color:#4A0A24
  classDef out fill:#F3E5F5,stroke:#6A1B9A,color:#2E0B40
  style HOSP fill:#FAFBFC,stroke:#78909C
  style EDGE fill:#F4F9FE,stroke:#1E63B5
  style GKE fill:#F6FBF6,stroke:#2E7D32
```

## Legend

| Colour | Meaning |
|---|---|
| Grey | Hospital source system |
| Blue | Hydra Connect — runs inside the hospital network |
| Green | Hydra Platform — runs on GKE europe-west1 |
| Yellow (cylinder) | Storage |
| Red (cylinder) | Dead-letter queue |
| Pink (rounded) | Customer team member |
| Purple | Consumer of the result |

## Why each non-obvious box is there

| Box | Why it is there |
|---|---|
| File drop folder | The HIS cannot push over the network; it writes files. Connect must detect half-written and duplicate files. |
| Hydra Connect edge node | No public ingress: something must run inside the hospital to reach the three sources and push data out. |
| One channel per source | Each source speaks differently (files, FHIR REST, openEHR AQL); a channel only handles syntax, never meaning. |
| Ch.3 openEHR | Whether it enters before week 12 depends on the agreed first cohort (e.g. diabetes needs it, oncology does not). |
| Validate + normalise | One shared, documented canonical event format, so Platform does not care where the data came from. Z-segments are kept, not dropped. |
| Local DLQ | Unparseable messages are parked with a reason — never silently lost, never blocking the rest. |
| Pseudonymisation (edge) | Done inside the hospital with a key the hospital owns: no direct identifiers ever reach the cloud. Same patient → same pseudonym, so identity resolution still works. |
| Outbound TLS only (443) | Marta opens one outbound rule; nothing opens inbound. |
| Landing store | Pseudonymised events are kept, so any downstream step can be replayed without asking the hospital again. |
| Patient identity resolution | The same patient can arrive from three systems; Person must have exactly one row. Doubtful matches are never merged automatically. |
| Working store | Platform's own reference data: cross-reference table, vocabulary, quarantine, review items. Researchers never see it. |
| Vocabulary mapping | CIE-10-ES and local lab codes must become OMOP standard concepts; kept separate and versioned so it changes without touching channels. |
| Review queue | Doubtful matches and unmapped codes are decided by a person (Dr. Ruiz), not guessed by the pipeline. |
| Data quality checks | Implausible values (e.g. future dates) are quarantined so they never reach the cohort. |
| OMOP loader (idempotent) | Re-running a load never creates duplicate rows. |
| Operations console | Diego must see what failed and replay it alone after handover — covers Connect channels and Platform steps. |
| OMOP CDM — owned by San Andrés | The research repository belongs to the hospital, outside Hydra Platform. Hosting location is an open question (see pre-kickoff questions). |

# CLAUDE.md — Parte 2 de la prueba técnica (HL7 + FHIR → OMOP en C#)

Contexto para Claude Code en VS Code. **Léelo entero antes de escribir código.**

## Quién y cómo

- **Alonso** es Senior .NET (C#, SQL Server, HL7 v2 / Mirth; varios años integrando el HIS Casiopea). Habla en **español**: responde en español, con frases cortas y ejemplos concretos. Expande cada acrónimo la primera vez.
- **El código, los comentarios, los commits y el README van en inglés.**
- **Regla principal:** el enunciado penaliza *"AI-generated code the candidate cannot defend in stage 2"*. En la fase 2, Alonso tendrá que explicar el código y ampliarlo en directo. Por eso:
  - Trabaja **por bloques pequeños** (ver "Plan de bloques"). Al terminar cada bloque, explícalo en sencillo y deja **2-3 preguntas tipo fase 2** para que Alonso las conteste. **No pases al siguiente bloque hasta que Alonso lo confirme.**
  - Si Alonso quiere escribir él una pieza, guíale sin dársela hecha.
  - Nada de código "por si acaso". Cada clase tiene que justificar su existencia.
  - Tests que prueben algo real. En una prueba anterior un test generado no probaba nada.
- **Solo la Parte 2.** La Parte 1 (`/part1`) está cerrada. No la toques ni mezcles sus datos. Al final habrá una revisión global aparte.
- Commits pequeños, uno por bloque, con mensajes en inglés.

## El enunciado (Parte 2, literal)

> Build a small project under /part2 that:
> - **HL7 ingestion component**: ingests a small HL7 v2.5 ADT batch and a FHIR R4 bundle (samples provided in /part2/data). Normalizes both to a shared canonical event format (JSONL on disk). Treats the feed as untrusted: reads MSH for separators, does not silently drop Z-segments, lands unparseable messages in /out/dlq.
> - **OMOP writer component**: reads the canonical JSONL and lands it into OMOP tables (Person, Visit_occurrence, Condition_occurrence) in SQLite or DuckDB — no Postgres setup required. Includes vocab-mapping stubs for at least one ICD-10 → OMOP standard_concept_id (concept IDs can be invented; what matters is the table structure and the mapping pattern).
> - **Handoff**: the two components communicate via the canonical JSONL on disk, simulating a Hydra channel boundary. Each component must run independently with a single command.
> - **openEHR**: you do not need to implement it. In README, sketch how you would slot it in.

**El README de `/part2` tiene que cubrir:**
- cómo ejecutar cada componente (un comando cada uno) y un script de extremo a extremo;
- qué lenguaje y por qué, en 1-2 líneas;
- por qué las fronteras están donde están;
- cómo se ampliaría para openEHR y qué cambia para producción multi-tenant en Hydra: tenant stamping, observability, DLQ replay y compliance posture.

**Rúbrica:**

| Dimensión | Peso | Qué se valora |
|---|---|---|
| Code craft | 40 % | Idiomático, tipado, un módulo claro de carga OMOP, no un notebook. Elección de lenguaje justificada |
| Boundary design | 20 % | **El formato canónico es el artefacto**: documentado. Cada componente se puede cambiar o relanzar |
| OMOP modeling | 15 % | Tablas y cardinalidad correctas, unión persona/visita sensata, mapeo de vocabulario enchufable |
| Pragmatism | 15 % | Sin sobreingeniería. README honesto sobre lo que se dejó fuera |
| Forward-deployed | 10 % | El README trata multi-tenant, observabilidad, DLQ replay y cumplimiento |

**Anti-patrones que marcan:** separadores HL7 fijos en el código (hard-coded) · Z-segments descartados en silencio · notebooks · código de IA que el candidato no sabe defender · sobreingeniería (*"No Kafka, no service mesh, no DDD layers"*).

**Fase 2 (2 h, en directo):** explicar el código y **una ampliación en vivo**, que puede ser **multi-tenant tagging** o **DLQ replay with dedup**. El diseño tiene que dejar ambas fáciles, sin implementarlas ahora.

**Tiempo:** 3-5 h en total para todo el ejercicio. **Entrega: martes 20-oct-2026**, a david@tietai.com.

## Datos (`part2/data/`)

- `adt_feed.hl7`: unos 20 mensajes separados por una línea en blanco, con segmentos separados por `\n`. Hay que **tolerar también `\r`**.
- `fhir_bundle.json`: un Bundle de tipo collection, 26 recursos (Patient, Encounter, Condition, Observation, MedicationStatement).
- `vocab_icd10.csv`: `source_code, source_vocabulary_id, source_name, target_concept_id, target_vocabulary_id, target_concept_name, domain_id`. Imita la relación "Maps to" de OMOP.
- `FIXTURES_README.md`: el README original de los datos. Lo que dice: *"If you spot more, tell us"*, *"Find what you find. Decide what to quarantine vs. let through. Tell us why"*, *"We want to see what your loader does when a source code has no standard_concept_id"*.

### Hallazgos ya identificados (comprobados contra los ficheros)

| Qué | Dónde | Notas |
|---|---|---|
| Separadores no estándar `#` (campo) y `@` (componente) | `ADT00010` (línea 65) | Anunciado por el README |
| **El mismo mensaje se contradice:** declara `@` como componente, pero `MSH-9` usa `^` (`ADT^A01^ADT_A01`) | `ADT00010` | No lo anuncia el README: **ponerlo en el nuestro** |
| Mensaje basura sin `MSH` | `GARBLED\|TRANSMISSION\|…` (línea 71) | → DLQ con el motivo |
| `MSH-10` duplicado | `ADT00013` dos veces (14:25 y 17:15), pacientes distintos | Hay que decidir clave e idempotencia |
| Falta `PID-7` (fecha de nacimiento) | `ADT00009`, paciente `100089012` (GOMEZ ROBERTO) | Ver P1 |
| Byte `0x07` (BEL) | Dentro de un **Z-segment** `ZPV` de texto libre, `ADT00014` (línea 103) | Ver P4 |
| Z-segments `ZIN` (aseguradora) y `ZPV` (procedencia/texto libre) | Varios mensajes | No descartarlos en silencio |
| **Dos identificadores de emisor para el mismo hospital:** `SAN_ANDRES` (PID-3 en HL7) y `urn:oid:2.16.724.4.40.100.1` (FHIR) | HL7 + FHIR | Pacientes compartidos: `100034567` y `100123456` (los P003 y P011 del README). Hace falta una regla de equivalencia para que salga **una** fila en `person` |
| Encounters sin fecha de fin | 4 de 5 en FHIR | Ver P2 |
| Valores imposibles: HbA1c 25 %, peso 0,5 kg, fecha 2099 | `Observation` en FHIR | Ver P3 |
| Código `C18.99` inventado y `C25.9` fuera del CSV | Condition | Carga con `concept_id = 0` (D11) |

Si aparecen más hallazgos, se apuntan aquí y en el README.

## Decisiones tomadas (no reabrir sin hablarlo con Alonso)

| # | Decisión | Por qué |
|---|---|---|
| D1 | **C#** (no Python ni Go) | Es donde Alonso escribe código de producción y puede defender cada línea. Se justifica en 1-2 líneas en el README |
| D2 | La Parte 3 (agente DQ) queda fuera | El enunciado recibido solo tiene 2 partes. Se documenta en el README |
| D4 | **Dos aplicaciones de consola por lotes**. Sin API, colas ni servicio escuchando | El enunciado pide JSONL en disco y un comando por componente |
| D5 | **El JSONL es la frontera.** El programa 2 lee el fichero; no recibe eventos | Cada programa se relanza por separado y se puede ver en qué lado falló |
| D6 | **El JSONL no se borra al leerlo** | Permite repetir la carga (replay) |
| D7 | **Cargador idempotente con clave de origen única** (`*_source_value` con `UNIQUE` + `INSERT OR IGNORE`, o un upsert) | Relanzar no duplica filas y prepara el "DLQ replay with dedup". **Pendiente: decidir la clave de cada tabla** (ojo con `ADT00013` duplicado) |
| D8 | **Parser HL7 propio**, sin NHapi | Se evalúa explícitamente leer los separadores del `MSH`. Unas 60 líneas propias se defienden enteras |
| D9 | **FHIR con `System.Text.Json`**, sin el SDK de Firely | Solo se necesitan unos pocos campos |
| D10 | **Mapeo detrás de una interfaz `IConceptMapper`** (hoy lee el CSV) | El enunciado pide el mapeo como pieza enchufable |
| D11 | **Código sin mapeo → la fila entra con `condition_concept_id = 0`** y el original en `condition_source_value` | Convención de OMOP. Comprobar en la documentación oficial de OMOP CDM 5.4 |

**Del diseño de la Parte 1, para que encaje (sin mezclar los datos):** el evento canónico es un JSON propio y documentado. Lleva un campo para lo no mapeado (Z-segments, extensiones) para no perder nada, y está pensado para llevar un `tenant_id` más adelante (ampliación de la fase 2).

## Decisiones pendientes (Alonso decide; abajo la recomendación)

| # | Problema | Opciones | Recomendación |
|---|---|---|---|
| P1 | Sin `PID-7`, y `year_of_birth` es obligatorio en OMOP | a) cuarentena con motivo · b) inventar un año | **a)** Nunca inventar datos clínicos. Rechazado en el cargador con el motivo (no DLQ: el mensaje sí se pudo leer) |
| P2 | `visit_end_date` es obligatorio y 4 de 5 Encounters están abiertos | a) fecha de extracción · b) misma fecha que el inicio · c) cuarentena | **b)** Con a), relanzar otro día da otro resultado y rompe la idempotencia. Documentarlo |
| P3 | Valores imposibles en `Observation` | Measurement no se pide | **No cargar.** README: "found, out of scope" |
| P4 | `0x07` en un Z-segment | a) limpiar y seguir · b) mensaje entero a la DLQ · c) aceptar el mensaje y marcar el campo | Recomendación original: **b)**. **Matiz nuevo:** el byte está en texto libre de un Z-segment, no en un dato clínico, así que mandar el ingreso entero a la DLQ pierde una visita válida. Pesar **c)** (conservar el valor en crudo y añadir un aviso de calidad al evento). Lo decide Alonso |

Cuando Alonso decida, se pasa la fila a "Decisiones tomadas".

## Plan de bloques (uno por commit; parar al final de cada uno)

1. **Esqueleto + formato canónico.** Solución `.sln` con `Ingest` (consola), `OmopWriter` (consola), una librería compartida **solo si hace falta** y `Tests` (xUnit). Contrato del evento canónico documentado en `part2/docs/canonical-event.md` con un ejemplo.
2. **Parser HL7.** Separadores leídos de `MSH-1`/`MSH-2`; tolera `\r` y `\n`; Z-segments conservados en el evento; los mensajes ilegibles van a `out/dlq/` con el motivo. Tests con `ADT00010` y `GARBLED`.
3. **Lector FHIR.** Patient, Encounter y Condition → eventos canónicos. Lo que se descarta y por qué queda contado.
4. **Escritor OMOP.** Esquema SQLite de `person`, `visit_occurrence` y `condition_occurrence`; `IConceptMapper` + CSV; carga idempotente; unión persona/visita; un solo `person` por paciente real (regla de equivalencia entre emisores).
5. **Script de extremo a extremo + README de `/part2`.** Un comando por componente; un script que lo ejecuta todo; secciones del README que pide el enunciado.

**Antes de empezar el bloque 1, confirma con Alonso** la versión de .NET (8 o 10), el nombre de los proyectos y las respuestas a P1-P4.

## Lo que Alonso tiene que comprobar él mismo (para "what I double-checked myself")

- [ ] Los campos obligatorios de las tres tablas, contra **OMOP CDM 5.4** (documentación de OHDSI), no contra lo que diga la IA.
- [ ] Los `concept_id` de sexo (8507/8532) y de tipo de visita (9201/9202/9203).
- [ ] Que el parser lee de verdad los separadores del `MSH`, probándolo con `ADT00010`.
- [ ] Que relanzar el programa 2 dos veces deja el mismo número de filas.
- [ ] Que cada comentario dice lo que el código hace de verdad.
- [ ] Que cada test prueba algo real.

## Registro de uso de IA

Apunta en `part2/AI_LOG.md` (en español, breve) cada propuesta de la IA que Alonso **rechace o corrija**, con la fecha y el motivo. Sirve para la sección del README sobre IA.

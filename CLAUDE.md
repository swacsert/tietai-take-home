# CLAUDE.md — Parte 2 de la prueba técnica (HL7 + FHIR → OMOP en C#)

Contexto para Claude Code en VS Code. **Léelo entero antes de escribir código.**

## Quién y cómo

- **Alonso** es Senior .NET (C#, SQL Server, HL7 v2 / Mirth; varios años integrando el HIS Casiopea). Habla en **español**: responde en español, con frases cortas y ejemplos concretos. Expande cada acrónimo la primera vez.
- **El código, los comentarios, los commits y el README van en inglés.**
- **Regla principal:** el enunciado penaliza *"AI-generated code the candidate cannot defend in stage 2"*. En la fase 2, Alonso tendrá que explicar el código y ampliarlo en directo. Por eso:
  - **Forma de trabajo desde el 2026-10-18 (por tiempo, entrega el martes 20):** se generan los bloques 2-5 seguidos, **sin parar** entre bloques, con un commit por bloque. **La revisión para la defensa se hace después**, sobre el código terminado: Alonso repasa cada clase y cada decisión que tomó la IA (marcadas como "decidida por la IA" en "Decisiones tomadas").
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
- `fhir_bundle.json`: un Bundle de tipo collection, **25 recursos** (4 Patient, 5 Encounter, 5 Condition, 7 Observation, 4 MedicationStatement). Corregido el 2026-10-18: antes decía 26, contando el propio Bundle.
- `vocab_icd10.csv`: `source_code, source_vocabulary_id, source_name, target_concept_id, target_vocabulary_id, target_concept_name, domain_id`. Imita la relación "Maps to" de OMOP.
- `FIXTURES_README.md`: el README original de los datos. Lo que dice: *"If you spot more, tell us"*, *"Find what you find. Decide what to quarantine vs. let through. Tell us why"*, *"We want to see what your loader does when a source code has no standard_concept_id"*.

### Hallazgos ya identificados (comprobados contra los ficheros)

| Qué | Dónde | Notas |
|---|---|---|
| Separadores no estándar `#` (campo) y `@` (componente) | `ADT00010` (línea 65) | Anunciado por el README |
| **El mismo mensaje se contradice:** declara `@` como componente, pero `MSH-9` usa `^` (`ADT^A01^ADT_A01`) | `ADT00010` | No lo anuncia el README: **ponerlo en el nuestro** |
| Mensaje basura sin `MSH` | `GARBLED\|TRANSMISSION\|…` (línea 71) | → DLQ con el motivo |
| `MSH-10` duplicado | `ADT00013` dos veces (14:25 y 17:15) | **Corregido 2026-10-18:** es el **mismo** paciente (`100234567`), la misma visita (`PV1-48` = 14:25) y el mismo `DG1`. Solo cambian `MSH-7` y `EVN-2`: es una retransmisión. `MSH-10` solo no sirve de clave |
| Falta `PID-7` (fecha de nacimiento) | `ADT00009`, paciente `100089012` (GOMEZ ROBERTO) | Ver P1 |
| Byte `0x07` (BEL) | Dentro de un **Z-segment** `ZPV` de texto libre, `ADT00014` (línea 103) | Ver P4 |
| Z-segments `ZIN` (aseguradora) y `ZPV` (procedencia/texto libre) | Varios mensajes | No descartarlos en silencio |
| **Dos identificadores de emisor para el mismo hospital:** `SAN_ANDRES` (PID-3 en HL7) y `urn:oid:2.16.724.4.40.100.1` (FHIR) | HL7 + FHIR | Pacientes compartidos: `100034567` y `100123456` (los P003 y P011 del README). Hace falta una regla de equivalencia para que salga **una** fila en `person` |
| Encounters sin fecha de fin | 4 de 5 en FHIR | Ver P2 |
| Valores imposibles: HbA1c 25 %, peso 0,5 kg, fecha 2099 | `Observation` en FHIR | Ver P3 |
| Código `C18.99` inventado y `C25.9` fuera del CSV | Condition | Carga con `concept_id = 0` (D11) |
| **La fecha de ingreso va en `PV1-48`, no en `PV1-44`** (estándar). `PV1-44` y `PV1-45` vienen vacíos | Los 16 `PV1` del feed | No lo anuncia el README. Ver P5 |
| **`PV1-19` (número de visita) contiene `VIP`**: los campos de `PV1` están desplazados | Los 16 `PV1` | No hay número de visita fiable: la clave de visita se deriva (paciente + fecha de ingreso) |
| 3 mensajes `ORU^R01` (resultados) dentro del feed ADT | `ORU00001-3` | Se leen bien; no generan visita ni diagnóstico. Sus `OBR`/`OBX` no se descartan en silencio |
| `A08` cambia el diagnóstico de una visita ya enviada | `ADT00015` (`R07.9` → `I21.4` sobre la visita de `ADT00002`) | Relevante para la clave de `condition_occurrence` |
| `A03` (alta) sin `PV1-45` | `ADT00011` | La hora de alta solo está en `EVN-2` / `MSH-7` |
| Patient sin `gender` | FHIR `p-013` (MORALES PATRICIA) | `gender_concept_id = 0`, no se inventa |
| El mismo código RxNorm `40048` con dos nombres (Capecitabine y Gemcitabine) | FHIR `MedicationStatement` | Fuera de alcance (no se pide `drug_exposure`). Solo README |
| Probable **misma visita en las dos fuentes** sin identificador común | `100034567`: HL7 09:01 (`O`) vs FHIR `enc-003-2` 09:00 (`AMB`). `100123456`: HL7 14:00 (`I`) vs FHIR `enc-011-1` 14:00 (`AMB`) | Ver P6 |
| Nombre distinto para el mismo paciente | `TORRES ELENA ISABEL` (HL7) vs `TORRES ELENA` (FHIR) | No afecta: la equivalencia va por identificador, no por nombre |
| `core.autocrlf=true`: en este Windows el `.hl7` sale con `\r\n`; en el repo está con `\n` | `adt_feed.hl7` | El parser tiene que tolerar `\r`, `\n` y `\r\n`. Tests con los tres |

Si aparecen más hallazgos, se apuntan aquí y en el README.

## Decisiones tomadas (no reabrir sin hablarlo con Alonso)

| # | Decisión | Por qué |
|---|---|---|
| D1 | **C#** (no Python ni Go) | Es donde Alonso escribe código de producción y puede defender cada línea. Se justifica en 1-2 líneas en el README |
| D2 | La Parte 3 (agente DQ) queda fuera | El enunciado recibido solo tiene 2 partes. Se documenta en el README |
| D4 | **Dos aplicaciones de consola por lotes**. Sin API, colas ni servicio escuchando | El enunciado pide JSONL en disco y un comando por componente |
| D5 | **El JSONL es la frontera.** El programa 2 lee el fichero; no recibe eventos | Cada programa se relanza por separado y se puede ver en qué lado falló |
| D6 | **El JSONL no se borra al leerlo** | Permite repetir la carga (replay) |
| D7 | **Cargador idempotente con clave de origen única** (`*_source_value` con `UNIQUE` + `INSERT OR IGNORE`, o un upsert) | Relanzar no duplica filas y prepara el "DLQ replay with dedup". **Claves (bloque 4):** `person` = `UNIQUE(person_source_value)` con `{emisor canónico}:{id}`; `visit_occurrence` = `UNIQUE(person_id, visit_source_value)` con `hl7:{visit.source_id}` o `fhir:{id}`; `condition_occurrence` se sustituye por visita (D23). Dedup de eventos en `etl_event` (D17) |
| D8 | **Parser HL7 propio**, sin NHapi | Se evalúa explícitamente leer los separadores del `MSH`. Unas 60 líneas propias se defienden enteras |
| D9 | **FHIR con `System.Text.Json`**, sin el SDK de Firely | Solo se necesitan unos pocos campos |
| D10 | **Mapeo detrás de una interfaz `IConceptMapper`** (hoy lee el CSV) | El enunciado pide el mapeo como pieza enchufable |
| D11 | **Código sin mapeo → la fila entra con `condition_concept_id = 0`** y el original en `condition_source_value` | Convención de OMOP. Comprobar en la documentación oficial de OMOP CDM 5.4 |
| D12 | **.NET 10** (`net10.0`). Proyectos `Part2.Ingest`, `Part2.OmopWriter`, `Part2.Tests` (xUnit). SQLite con `Microsoft.Data.Sqlite` | Solo están instalados los SDK 10.0.400 y 10.0.401 (comprobado el 2026-10-18) |
| D13 (era P1) | **Sin `PID-7` → cuarentena con motivo** en el cargador. Nunca se inventa el año | El mensaje se pudo leer (no es DLQ), pero OMOP exige `year_of_birth` |
| D14 (era P2) | **Visita sin fin → `visit_end_date` = fecha de inicio**, documentado en el README | Con la fecha de extracción, relanzar otro día daría otro resultado y rompería la idempotencia |
| D15 (era P3) | **No se carga Measurement.** Los valores imposibles se documentan como "found, out of scope" | El enunciado solo pide tres tablas |
| D17 | **`event_id` = `hl7:{MSH-3}:{MSH-10}` (sin `MSH-7`) + `content_hash`** (SHA-256 del mensaje con `MSH-7` y `EVN-2` vaciados). Mismo id + mismo hash = retransmisión, se ignora. Mismo id + hash distinto = conflicto, se marca y no se pisa. Lo aplica el writer | Con `MSH-7` en el id, las dos copias de `ADT00013` tendrían ids distintos y la dedup no vería la retransmisión |
| D18 | **`visit.source_id` HL7 = `{PID-3.1}:{fecha y hora de ingreso completa}`**, sin truncar | No hay número de visita fiable (`PV1-19` = `VIP`). Con solo la fecha se juntarían dos visitas del mismo día |
| D19 | **El ingestor cuenta los recursos FHIR que no cuelgan de ningún Encounter** (en el resumen de la ejecución, por tipo) | Que nunca se pierdan en silencio |
| D20 | Evento canónico v1 aprobado: librería `Part2.Canonical` (solo tipos + opciones JSON), hora local sin offset (en multi-tenant, zona horaria por tenant: README), sin nombres ni dirección (el JSONL contiene datos personales en `unmapped`: README) | Contrato en `part2/docs/canonical-event.md` |
| D21 (era P5) | **Fecha de ingreso: `PV1-44` si viene relleno; si no, `PV1-48`** con el aviso `admit_datetime_nonstandard_field` ("admit datetime in non-standard field") | Funciona con emisores estándar y con este, y queda constancia |
| D22 (era P6) | **No se unen visitas entre HL7 y FHIR**: dos filas en `visit_occurrence` del mismo `person`. Se documenta en el README como posible duplicado | Una unión falsa es peor que un duplicado |
| D23 (era P7; cambiada el 2026-10-18) | **Los diagnósticos de una visita se acumulan, nunca se borran**: los que ya están se quedan y los nuevos se añaden (`UNIQUE(visit_occurrence_id, condition_source_value)`). El writer sigue procesando por `sent_at`. La retirada explícita de un diagnóstico queda como pregunta al cliente en el README | La regla anterior (sustituir el conjunto) borraba el `E11.9` de GARCIA porque el `A03` solo reenvía `I50.9` |
| D24 | **Equivalencia de emisores en `part2/config/issuers.json`** (no en el código): `urn:oid:2.16.724.4.40.100.1` = `SAN_ANDRES`. `100034567` y `100123456` quedan como una sola fila en `person` cada uno | Es configuración del cliente, no lógica |
| D25 (decidida por la IA) | **`ADT00010` contradictorio:** se respetan los separadores declarados en `MSH-2` en todo el mensaje, nunca se adivinan. `MSH-9` (`ADT^A01^ADT_A01`) queda como un solo componente y el evento lleva el aviso `component_separator_mismatch`. El mensaje se acepta: el resto (`PID`, `PV1`, `DG1`) usa `@` de forma coherente | Adivinar separadores por mensaje sería justo lo que el enunciado penaliza. El único campo afectado es el tipo de mensaje, que no cambia nada de lo que se carga (es un `A01`) |
| D26 (corregida por Alonso el 2026-10-18) | **Enrutado por dominio.** `Condition` y `Symptom` van a `condition_occurrence` (en OMOP los síntomas son dominio Condition). `Observation` (`O80`, `Z51.11`) no se carga y se cuenta en el informe como "domain Observation: table out of scope", **no** como cuarentena. Sin mapeo → `concept_id = 0` (D11) | OMOP coloca cada registro en la tabla del dominio de su concepto; la tabla Observation está fuera de alcance |
| D27 | (Ya no aplica: con D23 los diagnósticos nunca se borran) | |
| D28 (confirmada por Alonso) | **Fin de visita: `PV1-45` si viene; si no, `EVN-2` con el aviso `discharge_datetime_from_evn`.** Solo en el `A03` | En un `A01`/`A04`/`A08`, `EVN-2` es la hora del ingreso o de la actualización, no la del alta: cerraría la visita al abrirla |
| D29 (Alonso) | **`PV1-10 = ER` → `emergency` → `visit_concept_id = 9203`**, aunque `PV1-2 = O`. `class_source` conserva `O` | Este HIS manda las urgencias como ambulatorias con el servicio `ER` (6 mensajes) |
| D30 (Alonso) | **Los motivos de la DLQ y la consola no citan el mensaje**: solo motivo, fichero y posición; el motivo nombra el campo, nunca su valor. Un error inesperado lleva el tipo de excepción, no su texto | El motivo acaba en logs; el valor puede ser un dato de paciente |
| D16 (era P4) | **`0x07` en el Z-segment: se acepta el mensaje**, se conserva el valor en crudo y se añade un aviso de calidad al evento | El byte está en texto libre, no en un dato clínico. Mandarlo a la DLQ perdería una visita válida |

**Del diseño de la Parte 1, para que encaje (sin mezclar los datos):** el evento canónico es un JSON propio y documentado. Lleva un campo para lo no mapeado (Z-segments, extensiones) para no perder nada, y está pensado para llevar un `tenant_id` más adelante (ampliación de la fase 2).

## Decisiones pendientes (Alonso decide; abajo la recomendación)

| # | Problema | Opciones | Recomendación |
|---|---|---|---|
(Ninguna abierta. P5-P7 pasaron a D21-D23 el 2026-10-18.)

Cuando Alonso decida, se pasa la fila a "Decisiones tomadas".

## Plan de bloques (uno por commit; los bloques 2-5 se hacen seguidos)

1. **Esqueleto + formato canónico.** Solución `.sln` con `Ingest` (consola), `OmopWriter` (consola), una librería compartida **solo si hace falta** y `Tests` (xUnit). Contrato del evento canónico documentado en `part2/docs/canonical-event.md` con un ejemplo.
2. **Parser HL7.** Separadores leídos de `MSH-1`/`MSH-2`; tolera `\r` y `\n`; Z-segments conservados en el evento; los mensajes ilegibles van a `out/dlq/` con el motivo. Tests con `ADT00010` y `GARBLED`.
3. **Lector FHIR.** Patient, Encounter y Condition → eventos canónicos. Lo que se descarta y por qué queda contado.
4. **Escritor OMOP.** Esquema SQLite de `person`, `visit_occurrence` y `condition_occurrence`; `IConceptMapper` + CSV; carga idempotente; unión persona/visita; un solo `person` por paciente real (regla de equivalencia entre emisores).
5. **Script de extremo a extremo + README de `/part2`.** Un comando por componente; un script que lo ejecuta todo; secciones del README que pide el enunciado.

Confirmado con Alonso el 2026-10-18: .NET 10, nombres de proyecto y P1-P4 (ahora D12-D16).

## Lo que Alonso tiene que comprobar él mismo (para "what I double-checked myself")

- [ ] Los campos obligatorios de las tres tablas, contra **OMOP CDM 5.4** (documentación de OHDSI), no contra lo que diga la IA.
- [ ] Los `concept_id` de sexo (8507/8532) y de tipo de visita (9201/9202/9203).
- [ ] Que el parser lee de verdad los separadores del `MSH`, probándolo con `ADT00010`.
- [ ] Que relanzar el programa 2 dos veces deja el mismo número de filas.
- [ ] Que cada comentario dice lo que el código hace de verdad.
- [ ] Que cada test prueba algo real.

## Registro de uso de IA

Apunta en `part2/AI_LOG.md` (en español, breve) cada propuesta de la IA que Alonso **rechace o corrija**, con la fecha y el motivo. Sirve para la sección del README sobre IA.

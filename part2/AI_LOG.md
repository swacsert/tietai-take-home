# AI log — Parte 2

Propuestas de la IA rechazadas o corregidas por Alonso (fecha · qué propuso · por qué se cambió).

| Fecha | Propuesta | Corrección |
|---|---|---|
| 2026-10-18 | El CLAUDE.md (redactado con IA) decía que las dos copias de `ADT00013` eran de pacientes distintos | Error de la IA, detectado al comprobar contra `adt_feed.hl7`: mismo paciente, misma visita y mismo `DG1`. Solo cambian `MSH-7` y `EVN-2`. Es una retransmisión |
| 2026-10-18 | `event_id` HL7 = `hl7:{MSH-3}:{MSH-10}:{MSH-7}` | Alonso: sin `MSH-7`, porque con él las dos copias de `ADT00013` dan ids distintos y la dedup no reconoce la retransmisión. Se añade `content_hash` (sin `MSH-7` ni `EVN-2`) para distinguir retransmisión de conflicto |
| 2026-10-18 | El evento canónico no contemplaba los recursos FHIR que no cuelgan de ningún Encounter | Alonso: el ingestor los cuenta en el resumen de la ejecución, para que no se pierdan en silencio |

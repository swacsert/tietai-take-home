# AI log — Parte 2

Propuestas de la IA rechazadas o corregidas por Alonso (fecha · qué propuso · por qué se cambió).

| Fecha | Propuesta | Corrección |
|---|---|---|
| 2026-10-18 | El CLAUDE.md (redactado con IA) decía que las dos copias de `ADT00013` eran de pacientes distintos | Error de la IA, detectado al comprobar contra `adt_feed.hl7`: mismo paciente, misma visita y mismo `DG1`. Solo cambian `MSH-7` y `EVN-2`. Es una retransmisión |
| 2026-10-18 | `event_id` HL7 = `hl7:{MSH-3}:{MSH-10}:{MSH-7}` | Alonso: sin `MSH-7`, porque con él las dos copias de `ADT00013` dan ids distintos y la dedup no reconoce la retransmisión. Se añade `content_hash` (sin `MSH-7` ni `EVN-2`) para distinguir retransmisión de conflicto |
| 2026-10-18 | El `content_hash` del ejemplo HL7 del bloque 1 lo calculó un script desechable de la IA | Error de la IA, detectado en el bloque 2 por el test que compara la salida del parser con el ejemplo: el script partía las líneas por `\r` y `\n` por separado, y con `\r\n` se quedaba solo con el `MSH`. Valor corregido y comprobado aparte con `awk` + `sha256sum` |
| 2026-10-18 | El CLAUDE.md (redactado con IA) decía que el bundle FHIR tiene 26 recursos | Error de la IA, detectado por un test en el bloque 3: son 25. El 26 contaba también el propio `Bundle` |
| 2026-10-18 | Regla D23 original: el conjunto de diagnósticos del mensaje más reciente sustituye al anterior (la propuso la IA como opción de P7) | Alonso: los diagnósticos se acumulan, nunca se borran. La regla de sustituir borraba el `E11.9` de GARCIA, porque el `A03` solo reenvía `I50.9`. La retirada explícita queda como pregunta al cliente |
| 2026-10-18 | D26 de la IA: los diagnósticos de dominio `Symptom` y `Observation` iban a la cuarentena | Alonso: `Symptom` se carga como Condition (en OMOP los síntomas son Condition). `Observation` no se carga y se cuenta como "table out of scope", no como cuarentena |
| 2026-10-18 | La IA mapeaba las urgencias solo por `PV1-2 = E`, así que las visitas `O` con `PV1-10 = ER` salían como 9202 | Alonso: `PV1-10 = ER` → 9203 |
| 2026-10-18 | Los motivos de la DLQ y la consola citaban hasta 40 caracteres del mensaje, y algunos el valor del campo (p. ej. una fecha de nacimiento) | Alonso: solo motivo, fichero y posición. Test que lo comprueba |
| 2026-10-18 | La IA escribió C# moderno: constructores primarios, expresiones de colección, `switch` con tuplas y patrones de propiedad, cadenas LINQ largas, `"""` | Alonso: estilo C# clásico, el que él escribe y defiende (campos `private readonly`, `new List<T>()`, `if`/`else if`, métodos cortos con nombre). Refactor sin cambio de comportamiento: misma salida byte a byte |
| 2026-10-18 | `GroupAsBundle`: la IA envolvía cada Encounter rechazado en un Bundle nuevo para la DLQ | Alonso: sobra. La DLQ guarda el JSON del Encounter tal cual; para reenviarlo se corrige y se vuelve a ingerir el bundle original |
| 2026-10-18 | El evento canónico no contemplaba los recursos FHIR que no cuelgan de ningún Encounter | Alonso: el ingestor los cuenta en el resumen de la ejecución, para que no se pierdan en silencio |

# Plan del proyecto — clips automáticos (tipo Nexus Clips)

**Objetivo del producto:** entrada = **un video**; salida = **clips cortos** centrados en los **momentos más importantes o relevantes** del contenido (highlights automáticos), en formato vertical y con subtítulos opcionales.

Documento vivo para **seguimiento de avance**. Marca las casillas cuando cada ítem esté terminado y probado en tu máquina.

**Leyenda de estado por etapa**

| Estado   | Significado                          |
|----------|--------------------------------------|
| ⬜       | No iniciado                          |
| 🟨       | En progreso                          |
| 🟩       | Completado                           |

Actualiza la fila **Estado** de cada etapa al cerrarla.

---

## Resumen de etapas

| # | Etapa | Estado |
|---|--------|--------|
| 0 | Fundamentos y entorno | 🟩 |
| 1 | Audio → transcripción y subtítulos | 🟩 |
| 2 | Detección de momentos (heurísticas) | 🟩 |
| 3 | Recorte y formato 9:16 | 🟩 |
| 4 | Subtítulos en el video | 🟩 |
| 5 | Interfaz y orquestación (WPF + API local) | 🟩 |
| 6 | Mejoras “inteligentes” (opcional / avanzado) | ⬜ |

---

## Etapa 0 — Fundamentos y entorno

**Objetivo:** Tener herramientas instaladas y un flujo mínimo reproducible (sin IA todavía si hace falta).

- [x] Repositorio clonado; Python 3.10+ y `venv` documentado
- [x] FFmpeg instalado y en `PATH` (`ffmpeg -version` OK)
- [x] Script que **extrae audio** (WAV/MP3) → `scripts/extract_audio.py`
- [x] Criterio de cierre: video/audio de entrada → archivo de audio válido (smoke test sintético OK)

**Notas / bloqueos:** _(en otra PC repetir `ffmpeg -version` y una prueba con tu video real)_

---

## Etapa 1 — Transcripción y subtítulos

**Objetivo:** Del audio (o video) obtener texto alineado en el tiempo y archivo de subtítulos.

- [x] Dependencias: `faster-whisper` en `pyproject.toml` / `pip install -e .`
- [x] CLI: `edite-ia` (pipeline) + módulo `edite_ia/transcribe.py` → `full.srt` y `full.vtt` en cada corrida
- [ ] Probar con fragmento corto y con archivo largo (memoria/tiempo) — *validación en tu hardware*
- [x] Modelo por defecto documentado: `small`, `device=cpu`, `compute_type=int8` (ver README; GPU opcional)
- [x] Criterio de cierre: subtítulos generados y guardados por corrida (reproducir en reproductor)

**Notas / bloqueos:**

---

## Etapa 2 — Detección de momentos (highlights simples)

**Objetivo:** Proponer **intervalos de tiempo** candidatos sin depender aún de LLM.

- [x] Carga de audio con **librosa**; RMS y **scipy.signal.find_peaks**
- [x] Picos sobre percentil configurable; ventana de duración ~`clip_length_sec`
- [x] Palabras clave en transcripción → refuerzo de score (`edite_ia/highlights.py` + CLI/API)
- [x] Salida: `highlights.json` con `(start, end, score)` ordenados por score
- [ ] Criterio subjetivo: 2–3 videos de prueba — *ajustar umbrales si hace falta*

**Notas / bloqueos:**

---

## Etapa 3 — Recorte y formato 9:16

**Objetivo:** Generar archivos de video cortos listos para vertical.

- [x] FFmpeg: **trim** por timestamps de highlights
- [x] Escalado + **crop** centrado a 1080×1920 (configurable)
- [x] Política documentada en README: **centro fijo** (sin tracking)
- [x] Criterio de cierre: clips en `run_*/clips/*.mp4` listos para móvil

**Notas / bloqueos:**

---

## Etapa 4 — Subtítulos en el video

**Objetivo:** El clip final lleva subtítulos visibles (no solo archivo aparte).

- [x] SRT por clip con tiempos **restados** al inicio del corte (`utterances_for_clip`)
- [x] Render FFmpeg `subtitles=` + `force_style` legible (`edite_ia/clip.py`)
- [x] Criterio de cierre: clip vertical con texto quemado

**Notas / bloqueos:** _En Windows, evitar rutas con caracteres raros en el filtro de subtítulos._

---

## Etapa 5 — Interfaz y orquestación

**Objetivo:** Flujo de usuario: elegir video → procesar → ver carpeta de salida.

- [x] **Backend Python:** FastAPI en `edite_ia/api.py` (`edite-ia-api`)
- [x] `POST /job`, `GET /job/{id}`, `GET /health`
- [x] **WPF:** `desktop/EditeIa.Desktop` — rutas, modelo, máx. clips, keywords, log y sondeo
- [x] Errores HTTP y `failed` con `error` en JSON / MessageBox
- [x] Criterio: procesar desde la UI sin terminal (backend aparte sí debe estar levantado)

**Notas / bloqueos:**

---

## Etapa 6 — Mejoras avanzadas (opcional)

**Objetivo:** Profundizar según tiempo del proyecto; no bloquea el núcleo.

- [ ] Análisis con **LLM local** (Ollama): resumir transcripción y sugerir timestamps
- [ ] Integración **OBS** / carpeta hot-folder / export pensado para Twitch
- [ ] Detección visual (MediaPipe / YOLO) para un juego o escena concreta
- [ ] Animaciones de subtítulo más elaboradas (ASS avanzado)
- [ ] Criterio de cierre: al menos **una** mejora demostrable en video final

**Notas / bloqueos:** _Ideas resumidas en README (sección Etapa 6)._

---

## Registro de avance (bitácora corta)

| Fecha | Etapa | Qué se hizo |
|-------|-------|-------------|
| 2026-05-10 | 0 | Script extracción, README, smoke test FFmpeg |
| 2026-05-10 | 1–5 | Paquete `edite_ia`, pipeline, CLI, API, WPF; `desktop/EditeIa.slnx` |
|       |       |             |

---

## Definición de “proyecto mínimo viable” (MVP)

Considerá el proyecto **entregable** cuando las etapas **0–4** estén en 🟩 (pipeline end-to-end sin UI), o **0–5** si la consigna exige interfaz.

---

*Última actualización del plan: implementación núcleo 0–5 en repo; Etapa 6 pendiente opcional.*

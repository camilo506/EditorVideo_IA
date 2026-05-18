# edite_ia

**Propósito principal:** vos pasás **un video largo** y la herramienta **detecta y recorta clips** en torno a los momentos **más importantes o relevantes** (audio + palabras clave + transcripción). Cada clip puede salir en **vertical 9:16**, **horizontal 16:9** o **ambos formatos** (elegís en la UI o por API/CLI).

Técnicamente: **faster-whisper**, heurísticas de energía en el audio (**librosa**), **FFmpeg** para recorte y formato, más una app **WPF** que habla con una **API local** en Python.

Plan por etapas y control de avance: [`docs/PLAN_ETAPAS.md`](docs/PLAN_ETAPAS.md).

## Requisitos

- **Python 3.10+**
- **FFmpeg** en el `PATH` (`ffmpeg -version`)
- **.NET 8 SDK** (solo para la UI WPF), Windows

## Instalación (Python)

```powershell
cd d:\GitHub\edite_ia
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
```

La primera ejecución con Whisper **descargará el modelo** elegido (p. ej. `small`; varios cientos de MB).

### GPU (opcional)

Con CUDA podés usar p. ej. `--device cuda --compute-type float16` en la CLI; en la API, los mismos campos en el JSON del `POST /job`.

## Uso rápido — línea de comandos

```powershell
edite-ia "D:\videos\entrada.mp4" -o "D:\salida" --model small --max-clips 5 --keywords "wow, no puede ser"
```

Horizontal 16:9:

```powershell
edite-ia "D:\videos\entrada.mp4" -o "D:\salida" --clip-layout horizontal
```

Vertical y horizontal (dos MP4 por cada momento):

```powershell
edite-ia "D:\videos\entrada.mp4" -o "D:\salida" --clip-layout both
```

Plantilla vertical (solo `clip_layout` vertical o la parte vertical de `both`):

```powershell
edite-ia "D:\videos\entrada.mp4" -o "D:\salida" --vertical-template split_half
edite-ia "D:\videos\entrada.mp4" -o "D:\salida" --vertical-template face_top
```

- **`full`** — un solo encuadre 9:16 (recorte centrado), como antes.  
- **`split_half`** — mitad superior e inferior del **mismo** video apiladas (50/50).  
- **`face_top`** — franja superior ~30% + panel inferior ~70% del **mismo** video (simula “cámara arriba + gameplay abajo” cuando la composición del original lo permite; **no** mezcla dos archivos distintos).  
- **`custom`** — dos recortes normalizados (`x`,`y`,`w`,`h` en 0–1) del **mismo** frame: uno para la franja superior (cámara) y otro para el panel inferior (contenido). En la **UI WPF** podés dibujarlos sobre un fotograma; por **API** enviá `custom_camera` y `custom_content`; en **CLI** se usan valores por defecto si no los pasás.

Sin subtítulos incrustados en los MP4 (igual se generan `full.srt` / `.srt` por clip si aplica):

```powershell
edite-ia "D:\videos\entrada.mp4" -o "D:\salida" --no-subs
```

Se crea una carpeta `run_YYYYMMDD_HHMMSS_<id>\` dentro de `D:\salida` con:

- `audio.wav`, `full.srt`, `full.vtt`, `utterances.json`, `highlights.json`
- `clips\clip_01.mp4`, … — por defecto **solo vertical** 1080×1920 (`clip_layout` = `vertical`).
- Con `clip_layout`: `horizontal` → `clip_01.mp4` en **1920×1080**. Con `both` → `clip_01_vertical.mp4` y `clip_01_horizontal.mp4`.

## API local (para la UI)

En una terminal con el venv activado:

```powershell
edite-ia-api
```

Por defecto escucha en `http://127.0.0.1:8765`.

- `GET /health` — comprobación
- `POST /job` — cuerpo JSON (snake_case), ejemplo:

```json
{
  "input_path": "D:/videos/entrada.mp4",
  "output_dir": "D:/salida",
  "model_size": "small",
  "device": "cpu",
  "compute_type": "int8",
  "clip_length_sec": 30,
  "max_clips": 8,
  "keywords": ["gol", "golazo"],
  "burn_subtitles": true,
  "clip_layout": "vertical",
  "vertical_template": "full"
}
```

Valores de `clip_layout`: `vertical` | `horizontal` | `both`.  
Valores de `vertical_template`: `full` | `split_half` | `face_top` | `custom` (solo afecta salidas 9:16). Con `custom`, opcional: `custom_camera` y `custom_content` como `{"x":0.25,"y":0.04,"w":0.38,"h":0.26}` (coordenadas normalizadas al video de entrada).

`burn_subtitles: false` genera clips sin texto incrustado; igual se crean `full.srt` / `full.vtt` y, por clip, un `.srt` al lado del `.mp4` si hay frases en ese tramo.

- `GET /job/{id}` — estado hasta `completed` o `failed`

## Interfaz WPF

Abrir la solución `desktop/EditeIa.slnx` en Visual Studio (o el `.csproj` directamente) o compilar:

```powershell
dotnet build desktop\EditeIa.Desktop\EditeIa.Desktop.csproj -c Release
```

Ejecutable: `desktop\EditeIa.Desktop\bin\Release\net8.0-windows\EditeIa.Desktop.exe`.  
Antes, dejá corriendo `edite-ia-api`. Si elegís la plantilla **Personalizado**, capturá un fotograma, arrastrá los rectángulos (cámara / contenido) y revisá la vista previa 9:16 antes de **Procesar** (hace falta `ffmpeg` en el PATH para extraer el fotograma).

## Etapa 0 (solo audio)

Sigue disponible el script independiente:

```powershell
python scripts\extract_audio.py entrada.mp4
```

## Arquitectura (resumen)

| Pieza | Rol |
|--------|-----|
| `edite_ia/audio.py` | WAV 16 kHz mono para análisis |
| `edite_ia/transcribe.py` | faster-whisper → frases con tiempos |
| `edite_ia/highlights.py` | picos RMS (librosa) + refuerzo por palabras clave |
| `edite_ia/layout.py` | Presets vertical / horizontal / ambos |
| `edite_ia/clip.py` | FFmpeg: recorte, escala 9:16 o 16:9 centrado, subtítulos |
| `edite_ia/pipeline.py` | Orquestación |
| `edite_ia/api.py` | FastAPI + trabajos en segundo plano |
| `desktop/EditeIa.Desktop` | Cliente WPF |

**Política de encuadre vertical:** escalado para cubrir 1080×1920 y **recorte central** (sin tracking de caras en esta versión).

## Etapa 6 (opcional)

Extensiones posibles: LLM local (Ollama) sobre la transcripción, hot-folder / OBS, detección visual (YOLO/MediaPipe), estilos ASS avanzados. No forman parte del núcleo actual.

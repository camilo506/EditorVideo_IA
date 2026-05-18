from __future__ import annotations

import argparse
import sys
from pathlib import Path

from edite_ia.pipeline import PipelineConfig, run_pipeline


def main() -> None:
    parser = argparse.ArgumentParser(
        description="A partir de un video largo, detecta momentos relevantes y exporta clips (vertical y/u horizontal, subtítulos opcionales).",
    )
    parser.add_argument("input", type=Path, help="Video de entrada (mp4, mkv, etc.)")
    parser.add_argument(
        "-o",
        "--output-dir",
        type=Path,
        required=True,
        help="Carpeta donde se creará una subcarpeta run_* con resultados",
    )
    parser.add_argument(
        "--model",
        default="small",
        help="Modelo faster-whisper (tiny, base, small, medium, large-v3, ...)",
    )
    parser.add_argument(
        "--device",
        default="cpu",
        help="cpu o cuda",
    )
    parser.add_argument(
        "--compute-type",
        default="int8",
        help="int8, float16, etc. (según device/modelo)",
    )
    parser.add_argument(
        "--language",
        default=None,
        help="Código ISO opcional para forzar idioma (ej. es)",
    )
    parser.add_argument(
        "--clip-len",
        type=float,
        default=30.0,
        help="Duración objetivo de cada clip en segundos",
    )
    parser.add_argument(
        "--max-clips",
        type=int,
        default=8,
        help="Máximo de clips a generar",
    )
    parser.add_argument(
        "--keywords",
        default="",
        help="Palabras clave separadas por coma (refuerzo de score)",
    )
    parser.add_argument(
        "--width",
        type=int,
        default=1080,
    )
    parser.add_argument(
        "--height",
        type=int,
        default=1920,
    )
    parser.add_argument(
        "--clip-layout",
        choices=("vertical", "horizontal", "both"),
        default="vertical",
        help="Formato de salida: vertical 9:16, horizontal 16:9, o ambos (dos archivos por momento)",
    )
    parser.add_argument(
        "--vertical-template",
        choices=("full", "split_half", "face_top", "custom"),
        default="full",
        help="Plantilla 9:16: full; split_half; face_top; custom (CLI usa recortes por defecto; la UI permite dibujarlos).",
    )
    parser.add_argument(
        "--no-subs",
        action="store_true",
        help="No quemar subtítulos en el MP4 (igual se generan full.srt / .vtt; por clip se guarda .srt aparte)",
    )
    args = parser.parse_args()

    kws = [k.strip() for k in args.keywords.split(",") if k.strip()]

    cfg = PipelineConfig(
        input_video=args.input,
        output_dir=args.output_dir,
        model_size=args.model,
        device=args.device,
        compute_type=args.compute_type,
        language=args.language,
        clip_length_sec=args.clip_len,
        max_clips=args.max_clips,
        keywords=kws,
        width=args.width,
        height=args.height,
        burn_subtitles=not args.no_subs,
        clip_layout=args.clip_layout,
        vertical_template=args.vertical_template,
    )

    def on_stage(name: str) -> None:
        print(f"[{name}]")

    try:
        res = run_pipeline(cfg, on_stage=on_stage)
    except Exception as e:
        print(f"Error: {e}", file=sys.stderr)
        sys.exit(1)

    print("Listo.")
    print(f"Carpeta: {res.work_dir}")
    print(f"Idioma: {res.language}")
    for p in res.clips:
        print(f"  {p}")


if __name__ == "__main__":
    main()

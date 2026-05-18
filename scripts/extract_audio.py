"""
Extrae audio de un archivo de video o audio (Etapa 0).
Por defecto: WAV mono 16 kHz (adecuado para Whisper en etapas siguientes).
Requiere FFmpeg en PATH.
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path


def _require_ffmpeg() -> str:
    exe = shutil.which("ffmpeg")
    if not exe:
        print(
            "Error: no se encontró 'ffmpeg' en el PATH.\n"
            "Instalá FFmpeg (https://ffmpeg.org/) y asegurate de que esté en el PATH.",
            file=sys.stderr,
        )
        sys.exit(1)
    return exe


def extract_audio(
    input_path: Path,
    output_path: Path,
    *,
    sample_rate: int,
    mono: bool,
    codec_wav: bool,
) -> None:
    input_path = input_path.resolve()
    if not input_path.is_file():
        print(f"Error: no existe el archivo de entrada: {input_path}", file=sys.stderr)
        sys.exit(1)

    output_path = output_path.resolve()
    output_path.parent.mkdir(parents=True, exist_ok=True)

    cmd: list[str] = [
        _require_ffmpeg(),
        "-hide_banner",
        "-loglevel",
        "error",
        "-y",
        "-i",
        str(input_path),
        "-vn",
    ]

    if codec_wav:
        cmd.extend(
            [
                "-acodec",
                "pcm_s16le",
                "-ar",
                str(sample_rate),
                "-ac",
                "1" if mono else "2",
            ]
        )
    else:
        cmd.extend(
            [
                "-acodec",
                "libmp3lame",
                "-q:a",
                "2",
                "-ar",
                str(sample_rate),
                "-ac",
                "1" if mono else "2",
            ]
        )

    cmd.append(str(output_path))

    try:
        subprocess.run(cmd, check=True)
    except subprocess.CalledProcessError as e:
        print(f"Error: FFmpeg falló (código {e.returncode}).", file=sys.stderr)
        sys.exit(e.returncode or 1)

    if not output_path.is_file() or output_path.stat().st_size == 0:
        print("Error: no se generó un archivo de salida válido.", file=sys.stderr)
        sys.exit(1)

    print(f"Listo: {output_path}")


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Extrae audio de video/audio con FFmpeg.",
    )
    parser.add_argument(
        "input",
        type=Path,
        help="Ruta al video o audio de entrada (mp4, mkv, mov, etc.)",
    )
    parser.add_argument(
        "-o",
        "--output",
        type=Path,
        default=None,
        help="Ruta de salida. Por defecto: mismo directorio que la entrada, "
        "mismo nombre con extensión .wav o .mp3",
    )
    parser.add_argument(
        "--mp3",
        action="store_true",
        help="Salida MP3 en lugar de WAV",
    )
    parser.add_argument(
        "--sample-rate",
        type=int,
        default=16000,
        help="Frecuencia de muestreo Hz (default: 16000, típico para ASR)",
    )
    parser.add_argument(
        "--stereo",
        action="store_true",
        help="Mantener estéreo (por defecto: mono)",
    )
    args = parser.parse_args()

    inp = args.input
    use_mp3 = args.mp3
    if args.output is None:
        ext = ".mp3" if use_mp3 else ".wav"
        out = inp.with_suffix(ext)
    else:
        out = args.output

    extract_audio(
        inp,
        out,
        sample_rate=args.sample_rate,
        mono=not args.stereo,
        codec_wav=not use_mp3,
    )


if __name__ == "__main__":
    main()

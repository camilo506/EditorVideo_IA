from __future__ import annotations

import subprocess
from pathlib import Path

from edite_ia.ffmpeg_util import require_ffmpeg


def extract_audio_wav(
    input_media: Path,
    output_wav: Path,
    *,
    sample_rate: int = 16000,
    mono: bool = True,
) -> Path:
    input_media = input_media.resolve()
    if not input_media.is_file():
        raise FileNotFoundError(input_media)

    output_wav = output_wav.resolve()
    output_wav.parent.mkdir(parents=True, exist_ok=True)

    cmd = [
        require_ffmpeg(),
        "-hide_banner",
        "-loglevel",
        "error",
        "-y",
        "-i",
        str(input_media),
        "-vn",
        "-acodec",
        "pcm_s16le",
        "-ar",
        str(sample_rate),
        "-ac",
        "1" if mono else "2",
        str(output_wav),
    ]
    subprocess.run(cmd, check=True)
    if not output_wav.is_file() or output_wav.stat().st_size == 0:
        raise RuntimeError("No se generó WAV válido.")
    return output_wav

from __future__ import annotations

import shutil
from pathlib import Path


def require_ffmpeg() -> str:
    exe = shutil.which("ffmpeg")
    if not exe:
        raise RuntimeError(
            "FFmpeg no está en el PATH. Instalalo desde https://ffmpeg.org/ y reiniciá la terminal."
        )
    return exe


def ffmpeg_escape_subtitle_path(path: Path) -> str:
    """Ruta para filtro subtitles= en Windows/Linux."""
    s = path.resolve().as_posix()
    return s.replace(":", "\\:")

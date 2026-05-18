from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class ClipRenderTarget:
    """Un archivo de salida: sufijo antes de .mp4 (ej. '_vertical') o None para clip_XX.mp4."""

    filename_suffix: str | None
    width: int
    height: int


# 16:9 horizontal estándar para YouTube / reproductores landscape.
DEFAULT_LANDSCAPE_W = 1920
DEFAULT_LANDSCAPE_H = 1080

_VALID = frozenset({"vertical", "horizontal", "both"})

# Plantilla solo para salida vertical (9:16): una sola fuente de video.
_VERTICAL_TEMPLATES = frozenset({"full", "split_half", "face_top", "custom"})


def normalize_vertical_template(value: str) -> str:
    v = (value or "full").strip().lower()
    if v not in _VERTICAL_TEMPLATES:
        raise ValueError(
            f"vertical_template inválido: {value!r}. "
            f"Usá 'full', 'split_half', 'face_top' o 'custom'."
        )
    return v


def normalize_clip_layout(value: str) -> str:
    v = (value or "vertical").strip().lower()
    if v not in _VALID:
        raise ValueError(
            f"clip_layout inválido: {value!r}. Usá 'vertical', 'horizontal' o 'both'."
        )
    return v


def render_targets_for_layout(
    clip_layout: str,
    *,
    vertical_width: int,
    vertical_height: int,
    landscape_width: int = DEFAULT_LANDSCAPE_W,
    landscape_height: int = DEFAULT_LANDSCAPE_H,
) -> list[ClipRenderTarget]:
    layout = normalize_clip_layout(clip_layout)
    if layout == "horizontal":
        return [ClipRenderTarget(None, landscape_width, landscape_height)]
    if layout == "both":
        return [
            ClipRenderTarget("_vertical", vertical_width, vertical_height),
            ClipRenderTarget("_horizontal", landscape_width, landscape_height),
        ]
    return [ClipRenderTarget(None, vertical_width, vertical_height)]

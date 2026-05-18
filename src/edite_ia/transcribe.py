from __future__ import annotations

from pathlib import Path

from edite_ia.models import Utterance


def transcribe_file(
    media_path: Path,
    *,
    model_size: str = "small",
    device: str = "cpu",
    compute_type: str = "int8",
    language: str | None = None,
) -> tuple[list[Utterance], str]:
    """Transcribe con faster-whisper. Devuelve (utterances, language_detected)."""
    from faster_whisper import WhisperModel

    model = WhisperModel(model_size, device=device, compute_type=compute_type)
    segments, info = model.transcribe(
        str(media_path),
        language=language,
        vad_filter=True,
    )
    utterances: list[Utterance] = []
    for seg in segments:
        utterances.append(
            Utterance(start=float(seg.start), end=float(seg.end), text=seg.text or "")
        )
    lang = info.language or "unknown"
    return utterances, lang
